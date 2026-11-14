using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Loudswitch.Interop;

/// <summary>
/// Loudness EQ를 즉시(서비스 재시작 없이) 쓰기 위한 비공개 <c>IPolicyConfig</c> COM.
///
/// GUI("적용")가 쓰는 것과 동일한 경로다. <c>SetPropertyValue(deviceId, bFxStore=TRUE, key, pv)</c>는
/// FxProperties 저장소에 쓰면서 속성 변경 알림을 발생시켜 audiodg의 APO가 즉시 재로드된다.
/// 라이브러리가 없어 직접 선언하며, vtable 순서가 정확해야 한다(ThiefMaster/tartakynov 레퍼런스).
///
/// NativeAOT 호환을 위해 소스 생성 COM(<c>[GeneratedComInterface]</c>) + ComWrappers를 사용한다(<see cref="Com"/>).
/// 호출하지 않는 vtable 슬롯 0~8은 슬롯을 맞추기 위한 placeholder다.
/// </summary>
internal static class PolicyConfig
{
    private static readonly Guid CLSID_PolicyConfig = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid IID_IPolicyConfig = new("f8679f50-850a-41cf-9c72-430f290290c8");

    // MFPKEY_CORR_LOUDNESS_EQUALIZATION_ON ({GUID},PID)
    private static readonly PropertyKey LoudnessKey =
        new() { FormatId = new Guid("fc52a749-4be9-4510-896e-966ba6525980"), PropertyId = 3 };

    /// <summary>
    /// 지정 엔드포인트의 Loudness EQ를 켜거나 끈다. 반환값은 HRESULT(0 = S_OK).
    /// </summary>
    /// <param name="endpointId">
    /// 전체 엔드포인트 ID 문자열. 예: <c>{0.0.0.00000000}.{GUID}</c> (장치 GUID만으로는 안 됨).
    /// </param>
    public static int SetLoudness(string endpointId, bool on)
    {
        IPolicyConfig client = Com.Create<IPolicyConfig>(in CLSID_PolicyConfig, in IID_IPolicyConfig);
        PropertyKey key = LoudnessKey;
        PropVariant pv = PropVariant.Bool(on);
        return client.SetPropertyValue(endpointId, bFxStore: true, ref key, ref pv);
    }
}

// IID_IPolicyConfig (bFxStore 변형).
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
internal partial interface IPolicyConfig
{
    // 0~8 — 미사용(슬롯 유지). placeholder는 마샬 가능한 시그니처이기만 하면 된다.
    [PreserveSig] int GetMixFormat(nint a, nint b);                                                   // 0
    [PreserveSig] int GetDeviceFormat(nint a, int b, nint c);                                         // 1
    [PreserveSig] int ResetDeviceFormat(nint a);                                                      // 2
    [PreserveSig] int SetDeviceFormat(nint a, nint b, nint c);                                        // 3
    [PreserveSig] int GetProcessingPeriod(nint a, int b, nint c, nint d);                             // 4
    [PreserveSig] int SetProcessingPeriod(nint a, nint b);                                            // 5
    [PreserveSig] int GetShareMode(nint a, nint b);                                                   // 6
    [PreserveSig] int SetShareMode(nint a, nint b);                                                   // 7
    [PreserveSig] int GetPropertyValue(nint a, [MarshalAs(UnmanagedType.Bool)] bool b, nint c, nint d); // 8

    // 9 — 사용
    [PreserveSig] int SetPropertyValue(
        string deviceId,
        [MarshalAs(UnmanagedType.Bool)] bool bFxStore,
        ref PropertyKey key,
        ref PropVariant pv);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
}

/// <summary>
/// 최소 PROPVARIANT. x64에서 PROPVARIANT는 24바이트이며 vt는 오프셋 0, 값(boolVal/포인터)은 오프셋 8에 있다.
/// 이 앱은 VT_BOOL(평준화 값)과 VT_CLSID(공식 API 활성화 인자: GUID 포인터)만 다룬다.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public short BoolVal;
    [FieldOffset(8)] public nint Ptr;

    public const ushort VT_EMPTY = 0;
    public const ushort VT_BOOL = 11;
    public const ushort VT_CLSID = 72;

    /// <summary>GetValue 등으로 받은 PROPVARIANT가 가진 메모리(문자열 등)를 해제한다.</summary>
    public static void Clear(ref PropVariant pv) => PropVariantNative.PropVariantClear(ref pv);

    // VARIANT_TRUE = -1(0xFFFF), VARIANT_FALSE = 0
    public static PropVariant Bool(bool value) =>
        new() { Vt = VT_BOOL, BoolVal = value ? (short)-1 : (short)0 };
}

internal static partial class PropVariantNative
{
    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(ref PropVariant pv);
}
