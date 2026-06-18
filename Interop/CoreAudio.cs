using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Loudswitch.Interop;

/// <summary>
/// 출력(렌더) 엔드포인트를 열거/조회하기 위한 최소한의 Core Audio COM. <b>읽기 전용</b>이다.
/// 장치 GUID는 절대 하드코딩하지 않고 여기서 동적으로 얻는다.
///
/// NativeAOT 호환을 위해 소스 생성 COM(<c>[GeneratedComInterface]</c>) + ComWrappers를 사용한다(<see cref="Com"/>).
/// 호출하지 않는 vtable 앞쪽 메서드들은 슬롯(인덱스)을 맞추기 위한 자리만 차지한다.
/// </summary>
internal static class CoreAudio
{
    public enum EDataFlow { Render, Capture, All }

    public enum ERole { Console, Multimedia, Communications }

    // DEVICE_STATE_* (mmdeviceapi.h)
    private const int DEVICE_STATE_ACTIVE = 0x00000001;
    private const int DEVICE_STATEMASK_ALL = 0x0000000F;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    /// <summary>
    /// 출력 엔드포인트들의 ID 목록. 기본값은 사용 중(활성) 장치만.
    /// 형식 예: <c>{0.0.0.00000000}.{e6327cad-dcec-4949-ae8a-991e976a79d2}</c>
    /// </summary>
    public static IReadOnlyList<string> GetRenderEndpointIds(bool activeOnly = true)
    {
        int mask = activeOnly ? DEVICE_STATE_ACTIVE : DEVICE_STATEMASK_ALL;

        IMMDeviceEnumerator enumerator = Com.Create<IMMDeviceEnumerator>(in CLSID_MMDeviceEnumerator, in IID_IMMDeviceEnumerator);

        int hr = enumerator.EnumAudioEndpoints(EDataFlow.Render, mask, out IMMDeviceCollection collection);
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

    /// <summary>
    /// 기본 멀티미디어 출력 장치의 엔드포인트 ID. 기본 장치가 없으면 <c>null</c>.
    /// </summary>
    public static string? GetDefaultRenderEndpointId()
    {
        IMMDeviceEnumerator enumerator = Com.Create<IMMDeviceEnumerator>(in CLSID_MMDeviceEnumerator, in IID_IMMDeviceEnumerator);

        if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device) != 0 || device is null)
            return null;

        return device.GetId(out string id) == 0 ? id : null;
    }
}

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(CoreAudio.EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(CoreAudio.EDataFlow dataFlow, CoreAudio.ERole role, out IMMDevice device);
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
    [PreserveSig] int Activate(in Guid iid, int clsCtx, nint activationParams, out nint iface); // 미사용(슬롯 유지)
    [PreserveSig] int OpenPropertyStore(int access, out nint properties);                        // 미사용(슬롯 유지)
    [PreserveSig] int GetId(out string id);
}
