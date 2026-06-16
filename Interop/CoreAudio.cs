using System.Runtime.InteropServices;

namespace Loudswitch.Interop;

/// <summary>
/// 출력(렌더) 엔드포인트를 열거/조회하기 위한 최소한의 Core Audio COM 선언.
///
/// 슬라이스 1은 <b>읽기 전용</b>이다. 장치 GUID는 절대 하드코딩하지 않고 여기서 동적으로 얻는다.
/// 호출하지 않는 vtable 앞쪽 메서드들은 슬롯(인덱스)을 맞추기 위한 자리만 차지하며,
/// 실제로 호출되지 않으므로 인자 마샬링은 placeholder(IntPtr)로 둔다.
/// </summary>
internal static class CoreAudio
{
    public enum EDataFlow { Render, Capture, All }

    public enum ERole { Console, Multimedia, Communications }

    // DEVICE_STATE_* (mmdeviceapi.h)
    private const int DEVICE_STATE_ACTIVE = 0x00000001;
    private const int DEVICE_STATEMASK_ALL = 0x0000000F;

    // CLSID_MMDeviceEnumerator
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    // IID_IMMDeviceEnumerator
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // vtable 0 — 사용 (전체 장치 열거)
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);

        // vtable 1 — 사용 (기본 장치 표시용)
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    }

    // IID_IMMDeviceCollection
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        // vtable 0
        [PreserveSig] int GetCount(out int count);

        // vtable 1
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    // IID_IMMDevice
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        // vtable 0 — 미사용 (슬롯 유지용)
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);

        // vtable 1 — 미사용 (슬롯 유지용)
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);

        // vtable 2 — 사용
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    /// <summary>
    /// 출력 엔드포인트들의 ID 목록. 기본값은 사용 중(활성) 장치만.
    /// 형식 예: <c>{0.0.0.00000000}.{e6327cad-dcec-4949-ae8a-991e976a79d2}</c>
    /// </summary>
    public static IReadOnlyList<string> GetRenderEndpointIds(bool activeOnly = true)
    {
        int mask = activeOnly ? DEVICE_STATE_ACTIVE : DEVICE_STATEMASK_ALL;

        var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        try
        {
            int hr = enumerator.EnumAudioEndpoints(EDataFlow.Render, mask, out IMMDeviceCollection collection);
            if (hr != 0 || collection is null)
                throw new InvalidOperationException($"오디오 엔드포인트 열거 실패 (HRESULT 0x{hr:X8}).");

            try
            {
                hr = collection.GetCount(out int count);
                if (hr != 0)
                    throw new InvalidOperationException($"엔드포인트 개수 조회 실패 (HRESULT 0x{hr:X8}).");

                var ids = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    if (collection.Item(i, out IMMDevice device) != 0 || device is null)
                        continue;
                    try
                    {
                        if (device.GetId(out string id) == 0 && !string.IsNullOrEmpty(id))
                            ids.Add(id);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
                return ids;
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// 기본 멀티미디어 출력 장치의 엔드포인트 ID. 기본 장치가 없으면 <c>null</c>.
    /// (목록에서 "기본 장치"를 표시하기 위한 용도)
    /// </summary>
    public static string? GetDefaultRenderEndpointId()
    {
        var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        try
        {
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device);
            if (hr != 0 || device is null)
                return null;

            try
            {
                return device.GetId(out string id) == 0 ? id : null;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }
}
