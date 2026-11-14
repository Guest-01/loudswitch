using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Loudswitch.Interop;

/// <summary>
/// 출력(렌더) 엔드포인트를 열거·조회하고 장치 변경 알림을 받기 위한 Core Audio COM(전부 공개 API).
///
/// 엔드포인트 ID는 Microsoft 문서대로 <b>불투명한 문자열</b>로만 다룬다 — 저장·비교·<c>GetDevice</c>에 그대로 쓰고
/// 내용을 파싱하지 않는다("형식이 정의되지 않았고 바뀔 수 있다", IMMDevice::GetId 문서).
///
/// NativeAOT 호환을 위해 소스 생성 COM(<c>[GeneratedComInterface]</c>) + ComWrappers를 사용한다(<see cref="Com"/>).
/// </summary>
internal static class CoreAudio
{
    public enum EDataFlow { Render, Capture, All }

    public enum ERole { Console, Multimedia, Communications }

    // DEVICE_STATE_* (mmdeviceapi.h)
    private const int DEVICE_STATE_ACTIVE = 0x00000001;
    private const int DEVICE_STATEMASK_ALL = 0x0000000F;

    private const int STGM_READ = 0x0;
    private const ushort VT_LPWSTR = 31;

    // PKEY_Device_FriendlyName (functiondiscoverykeys_devpkey.h) — 사운드 설정에 보이는 이름, 예: "헤드폰 (RØDE NT-USB Mini)"
    private static readonly PropertyKey PKEY_Device_FriendlyName =
        new() { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    internal static IMMDeviceEnumerator CreateEnumerator() =>
        Com.Create<IMMDeviceEnumerator>(in CLSID_MMDeviceEnumerator, in IID_IMMDeviceEnumerator);

    /// <summary>출력 엔드포인트들의 ID 목록(시스템이 주는 순서). 기본값은 사용 중(활성) 장치만.</summary>
    public static IReadOnlyList<string> GetRenderEndpointIds(bool activeOnly = true)
    {
        int mask = activeOnly ? DEVICE_STATE_ACTIVE : DEVICE_STATEMASK_ALL;

        int hr = CreateEnumerator().EnumAudioEndpoints(EDataFlow.Render, mask, out IMMDeviceCollection collection);
        if (hr != 0 || collection is null)
            throw new InvalidOperationException($"오디오 엔드포인트 열거 실패 (HRESULT 0x{hr:X8}).");

        hr = collection.GetCount(out int count);
        if (hr != 0)
            throw new InvalidOperationException($"엔드포인트 개수 조회 실패 (HRESULT 0x{hr:X8}).");

        var ids = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            // ComWrappers RCW는 GC가 정리한다(Marshal.ReleaseComObject는 소스 생성 COM에서 미지원).
            if (collection.Item(i, out IMMDevice device) != 0 || device is null)
                continue;
            if (device.GetId(out string id) == 0 && !string.IsNullOrEmpty(id))
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>기본 멀티미디어 출력 장치의 엔드포인트 ID. 기본 장치가 없으면 <c>null</c>.</summary>
    public static string? GetDefaultRenderEndpointId()
    {
        if (CreateEnumerator().GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device) != 0 || device is null)
            return null;

        return device.GetId(out string id) == 0 ? id : null;
    }

    /// <summary>엔드포인트 ID로 장치를 연다(분리된 장치도 열린다). 없으면 <c>null</c>.</summary>
    public static IMMDevice? GetDevice(string endpointId) =>
        CreateEnumerator().GetDevice(endpointId, out IMMDevice device) == 0 ? device : null;

    /// <summary>장치 이름(<c>PKEY_Device_FriendlyName</c>)을 장치 속성 저장소에서 읽는다. 실패 시 <c>null</c>.</summary>
    public static string? GetFriendlyName(string endpointId)
    {
        IMMDevice? device = GetDevice(endpointId);
        if (device is null || device.OpenPropertyStore(STGM_READ, out IPropertyStore? props) != 0 || props is null)
            return null;

        PropertyKey key = PKEY_Device_FriendlyName;
        if (props.GetValue(in key, out PropVariant pv) != 0)
            return null;
        try
        {
            return pv.Vt == VT_LPWSTR && pv.Ptr != 0 ? Marshal.PtrToStringUni(pv.Ptr) : null;
        }
        finally
        {
            PropVariant.Clear(ref pv);
        }
    }
}

/// <summary>
/// 장치 변경 알림 구독(<c>IMMNotificationClient</c>). 콜백은 시스템 스레드에서 오므로, 받은 쪽은 오래 붙잡지 말고
/// 바로 넘겨야 한다(문서: 콜백 안에서 대기·재등록 금지). Dispose 시 구독 해제.
/// </summary>
internal sealed class DeviceNotifications : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly EndpointNotificationClient _client;
    private bool _registered;

    /// <param name="defaultRenderChanged">기본 멀티미디어 출력 장치가 바뀜.</param>
    /// <param name="devicesChanged">장치 추가·제거·상태 변경.</param>
    public DeviceNotifications(Action defaultRenderChanged, Action devicesChanged)
    {
        _enumerator = CoreAudio.CreateEnumerator();
        _client = new EndpointNotificationClient(defaultRenderChanged, devicesChanged);
        _registered = _enumerator.RegisterEndpointNotificationCallback(_client) == 0;
    }

    public bool IsRegistered => _registered;

    public void Dispose()
    {
        if (_registered)
            _enumerator.UnregisterEndpointNotificationCallback(_client);
        _registered = false;
    }
}

[GeneratedComClass]
internal sealed partial class EndpointNotificationClient(Action defaultRenderChanged, Action devicesChanged) : IMMNotificationClient
{
    public int OnDeviceStateChanged(nint deviceId, uint newState) => Fire(devicesChanged);

    public int OnDeviceAdded(nint deviceId) => Fire(devicesChanged);

    public int OnDeviceRemoved(nint deviceId) => Fire(devicesChanged);

    public int OnDefaultDeviceChanged(CoreAudio.EDataFlow flow, CoreAudio.ERole role, nint defaultDeviceId) =>
        flow == CoreAudio.EDataFlow.Render && role == CoreAudio.ERole.Multimedia ? Fire(defaultRenderChanged) : 0;

    public int OnPropertyValueChanged(nint deviceId, PropertyKey key) => 0;

    private static int Fire(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // 시스템 스레드로 예외를 돌려보내지 않는다.
        }
        return 0;
    }
}

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(CoreAudio.EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(CoreAudio.EDataFlow dataFlow, CoreAudio.ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

// 장치 ID 인자는 호출자(시스템) 소유 문자열이라 nint로 받는다(우리가 해제하지 않음, 사용하지도 않음).
[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged(nint deviceId, uint newState);
    [PreserveSig] int OnDeviceAdded(nint deviceId);
    [PreserveSig] int OnDeviceRemoved(nint deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(CoreAudio.EDataFlow flow, CoreAudio.ERole role, nint defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged(nint deviceId, PropertyKey key);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, int clsCtx, nint activationParams, out nint iface); // EffectsPropertyStore가 사용
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore? properties);
    [PreserveSig] int GetId(out string id);
}
