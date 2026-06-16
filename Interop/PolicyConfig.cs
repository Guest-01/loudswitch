using System.Runtime.InteropServices;

namespace Loudswitch.Interop;

/// <summary>
/// Loudness EQ를 즉시(서비스 재시작 없이) 쓰기 위한 비공개 <c>IPolicyConfig</c> COM 선언.
///
/// GUI("적용")가 쓰는 것과 동일한 경로다. <c>SetPropertyValue(deviceId, bFxStore=TRUE, key, pv)</c>는
/// FxProperties 저장소에 쓰면서 속성 변경 알림을 발생시켜 audiodg의 APO가 즉시 재로드된다.
/// 라이브러리가 없어 직접 선언하며, vtable 순서가 정확해야 한다(ThiefMaster/tartakynov 레퍼런스).
///
/// 슬라이스 2는 <c>SetPropertyValue</c>만 호출한다. 현재 상태 읽기는 검증된 레지스트리 리더를
/// 재사용하므로 <c>GetPropertyValue</c>의 out PROPVARIANT 마샬링은 피한다(선언만 둠).
/// </summary>
internal static class PolicyConfig
{
    // MFPKEY_CORR_LOUDNESS_EQUALIZATION_ON ({GUID},PID)
    private static readonly PropertyKey LoudnessKey =
        new(new Guid("fc52a749-4be9-4510-896e-966ba6525980"), 3);

    /// <summary>
    /// 지정 엔드포인트의 Loudness EQ를 켜거나 끈다. 반환값은 HRESULT(0 = S_OK).
    /// </summary>
    /// <param name="endpointId">
    /// 전체 엔드포인트 ID 문자열. 예: <c>{0.0.0.00000000}.{GUID}</c> (장치 GUID만으로는 안 됨).
    /// </param>
    public static int SetLoudness(string endpointId, bool on)
    {
        var client = (IPolicyConfig)(object)new PolicyConfigClient();
        try
        {
            PropertyKey key = LoudnessKey;
            PropVariant pv = PropVariant.Bool(on);
            return client.SetPropertyValue(endpointId, bFxStore: true, ref key, ref pv);
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }
    }

    // CLSID_PolicyConfig
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    // IID_IPolicyConfig (bFxStore 변형). vtable 슬롯 0~7은 호출하지 않으므로 placeholder로 둔다.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);                       // 0
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);            // 1
        [PreserveSig] int ResetDeviceFormat(IntPtr a);                           // 2
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);         // 3
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d); // 4
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);               // 5
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);                      // 6
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);                      // 7

        // 8 — 선언만(미호출)
        [PreserveSig] int GetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Bool)] bool bFxStore,
            ref PropertyKey key,
            out PropVariant pv);

        // 9 — 사용
        [PreserveSig] int SetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Bool)] bool bFxStore,
            ref PropertyKey key,
            ref PropVariant pv);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;

        public PropertyKey(Guid formatId, int propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    /// <summary>
    /// VT_BOOL 전용 최소 PROPVARIANT. x64에서 PROPVARIANT는 24바이트이며
    /// vt는 오프셋 0, boolVal(VARIANT_BOOL)은 오프셋 8에 위치한다.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public short BoolVal;

        private const ushort VT_BOOL = 11;

        // VARIANT_TRUE = -1(0xFFFF), VARIANT_FALSE = 0
        public static PropVariant Bool(bool value) =>
            new() { Vt = VT_BOOL, BoolVal = value ? (short)-1 : (short)0 };
    }
}
