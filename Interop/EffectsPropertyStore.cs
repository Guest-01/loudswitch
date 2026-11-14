using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Loudswitch.Interop;

/// <summary>
/// Windows 11 공식 음향 효과 설정 API(<c>IAudioSystemEffectsPropertyStore</c>, 빌드 22000+)로
/// Loudness EQ 값을 읽고 쓴다.
///
/// 효과 설정 저장소는 APO가 정한 <b>컨텍스트 GUID</b>마다 Default/User/Volatile 세 층으로 나뉜다
/// (Microsoft Learn "Windows 11 APIs for Audio Processing Objects" — FxProperty Store Structure).
/// Windows 설정 화면도 여기의 User 층을 읽고 쓴다(2026-11-14 RØDE NT-USB Mini에서 실측).
///
/// 문서상 이 API는 패키지 앱의 제한된 권한(audioDeviceConfiguration)을 요구하지만, 이 PC에서는
/// 패키징·관리자 없이 읽기·쓰기가 모두 동작했다. 나중에 막히면 호출부가 옛 방식으로 넘어간다.
/// </summary>
internal static partial class EffectsPropertyStore
{
    /// <summary>
    /// Microsoft 기본 효과 모듈(WMALFXGFXDSP)의 컨텍스트 GUID. 공식 API에는 컨텍스트 목록을 얻는
    /// 방법이 없어 상수로 둔다. 이 PC의 출력 장치 13개 모두 같은 값이었다(다른 PC는 미확인).
    /// </summary>
    public static readonly Guid MicrosoftApoContext = new("b13412ee-07af-4c57-b08b-e327f8db085b");

    // IAudioSystemEffectsPropertyStore (Windows SDK 10.0.26100 mmdeviceapi.h)
    private static readonly Guid IID_IAudioSystemEffectsPropertyStore = new("302AE7F9-D7E0-43E4-971B-1F8293613D2A");

    // MFPKEY_CORR_LOUDNESS_EQUALIZATION_ON ({GUID},PID)
    private static readonly PropertyKey LoudnessKey =
        new() { FormatId = new Guid("fc52a749-4be9-4510-896e-966ba6525980"), PropertyId = 3 };

    private const int CLSCTX_INPROC_SERVER = 0x1;
    private const uint STGM_READ = 0x0;
    private const uint STGM_READWRITE = 0x2;

    /// <summary>읽기 결과.</summary>
    /// <param name="Available">공식 API로 이 장치·컨텍스트의 저장소를 열 수 있었는가.</param>
    /// <param name="Value">평준화 값. User 층 → 없으면 Default 층 순서. 둘 다 없으면 null.</param>
    /// <param name="Layer">값을 찾은 층 이름("User"/"Default"), 없으면 null.</param>
    public readonly record struct ReadResult(bool Available, bool? Value, string? Layer);

    public static ReadResult ReadLoudness(string endpointId)
    {
        IAudioSystemEffectsPropertyStore? store = Open(endpointId, out _);
        if (store is null)
            return new(false, null, null);

        bool anyLayer = false;
        foreach ((string name, bool user) in new[] { ("User", true), ("Default", false) })
        {
            int hr = user
                ? store.OpenUserPropertyStore(STGM_READ, out IPropertyStore? ps)
                : store.OpenDefaultPropertyStore(STGM_READ, out ps);
            if (hr != 0 || ps is null)
                continue; // 그 층이 없음(0x80070002 등)

            anyLayer = true;
            if (TryGetBool(ps, LoudnessKey, out bool value))
                return new(true, value, name);
        }

        // 층이 하나도 없으면 이 컨텍스트를 쓰지 않는 장치로 본다.
        return new(anyLayer, null, null);
    }

    /// <summary>User 층에 평준화 값을 쓰고 저장한다. 반환값은 HRESULT(0 = 성공).</summary>
    public static int WriteLoudness(string endpointId, bool on)
    {
        IAudioSystemEffectsPropertyStore? store = Open(endpointId, out int hr);
        if (store is null)
            return hr;

        hr = store.OpenUserPropertyStore(STGM_READWRITE, out IPropertyStore? ps);
        if (hr != 0 || ps is null)
            return hr != 0 ? hr : unchecked((int)0x80004005); // E_FAIL

        PropertyKey key = LoudnessKey;
        PropVariant pv = PropVariant.Bool(on);
        hr = ps.SetValue(in key, in pv);
        return hr != 0 ? hr : ps.Commit();
    }

    private static unsafe IAudioSystemEffectsPropertyStore? Open(string endpointId, out int hr)
    {
        hr = unchecked((int)0x80070490); // E_NOTFOUND
        IMMDevice? device;
        try
        {
            device = CoreAudio.GetDevice(endpointId);
        }
        catch (Exception ex)
        {
            hr = ex.HResult;
            return null;
        }
        if (device is null)
            return null;

        // 활성화 인자: 컨텍스트 GUID를 가리키는 VT_CLSID PROPVARIANT (공식 문서 샘플과 동일).
        Guid context = MicrosoftApoContext;
        PropVariant activation = new() { Vt = PropVariant.VT_CLSID, Ptr = (nint)(&context) };

        hr = device.Activate(in IID_IAudioSystemEffectsPropertyStore, CLSCTX_INPROC_SERVER, (nint)(&activation), out nint p);
        if (hr != 0 || p == 0)
            return null; // Windows 10(인터페이스 없음) 또는 정책으로 막힘 → 호출부가 옛 방식으로

        return Com.Wrap<IAudioSystemEffectsPropertyStore>(p);
    }

    private static bool TryGetBool(IPropertyStore ps, PropertyKey key, out bool value)
    {
        value = false;
        if (ps.GetValue(in key, out PropVariant pv) != 0)
            return false;
        try
        {
            if (pv.Vt != PropVariant.VT_BOOL)
                return false; // VT_EMPTY = 값 없음
            value = pv.BoolVal != 0;
            return true;
        }
        finally
        {
            PropVariant.Clear(ref pv);
        }
    }

    /// <summary>
    /// 이 장치의 평준화 값이 바뀌면(Windows 설정 화면, 다른 앱, 우리 자신의 쓰기 포함) <paramref name="changed"/>를 부른다.
    /// 공식 API(<c>RegisterPropertyChangeNotification</c>)를 쓸 수 없는 장치면 <c>null</c>.
    /// 콜백은 시스템 스레드에서 온다 — 받은 쪽은 바로 다른 스레드로 넘겨야 한다. Dispose 시 구독 해제.
    /// </summary>
    public static IDisposable? WatchLoudness(string endpointId, Action changed)
    {
        IAudioSystemEffectsPropertyStore? store = Open(endpointId, out _);
        if (store is null)
            return null;

        var client = new EffectsChangeClient(LoudnessKey, changed);
        return store.RegisterPropertyChangeNotification(client) == 0 ? new Subscription(store, client) : null;
    }

    private sealed class Subscription(IAudioSystemEffectsPropertyStore store, EffectsChangeClient client) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            store.UnregisterPropertyChangeNotification(client);
        }
    }
}

[GeneratedComClass]
internal sealed partial class EffectsChangeClient(PropertyKey watched, Action changed) : IAudioSystemEffectsPropertyChangeNotificationClient
{
    public int OnPropertyChanged(int storeType, PropertyKey key)
    {
        if (key.FormatId == watched.FormatId && key.PropertyId == watched.PropertyId)
        {
            try
            {
                changed();
            }
            catch
            {
                // 시스템 스레드로 예외를 돌려보내지 않는다.
            }
        }
        return 0;
    }
}

// IAudioSystemEffectsPropertyStore (Windows SDK 10.0.26100 mmdeviceapi.h, 메서드 순서 그대로)
[GeneratedComInterface]
[Guid("302AE7F9-D7E0-43E4-971B-1F8293613D2A")]
internal partial interface IAudioSystemEffectsPropertyStore
{
    [PreserveSig] int OpenDefaultPropertyStore(uint stgmAccess, out IPropertyStore? store);
    [PreserveSig] int OpenUserPropertyStore(uint stgmAccess, out IPropertyStore? store);
    [PreserveSig] int OpenVolatilePropertyStore(uint stgmAccess, out IPropertyStore? store);
    [PreserveSig] int ResetUserPropertyStore();
    [PreserveSig] int ResetVolatilePropertyStore();
    [PreserveSig] int RegisterPropertyChangeNotification(IAudioSystemEffectsPropertyChangeNotificationClient callback);
    [PreserveSig] int UnregisterPropertyChangeNotification(IAudioSystemEffectsPropertyChangeNotificationClient callback);
}

// 콜백: type = AUDIO_SYSTEMEFFECTS_PROPERTYSTORE_TYPE(0 Default, 1 User, 2 Volatile)
[GeneratedComInterface]
[Guid("20049D40-56D5-400E-A2EF-385599FEED49")]
internal partial interface IAudioSystemEffectsPropertyChangeNotificationClient
{
    [PreserveSig] int OnPropertyChanged(int storeType, PropertyKey key);
}

// IPropertyStore (propsys.h) — Windows 표준 속성 저장소.
[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(in PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(in PropertyKey key, in PropVariant value);
    [PreserveSig] int Commit();
}
