using System.Text;
using Microsoft.Win32;

namespace Loudswitch.Audio;

/// <summary>
/// 엔드포인트의 사람이 읽을 수 있는 이름을 레지스트리 <c>Properties</c>에서 best-effort로 읽는다 (읽기 전용).
/// 이름은 보조 정보이므로 못 읽어도 치명적이지 않고 "(이름 없음)"으로 대체한다.
/// </summary>
internal static class EndpointName
{
    // PKEY_Device_FriendlyName — 사운드 설정에 보이는 전체 이름 (예: "스피커 (Realtek...)")
    private const string FriendlyName = "{a45c254e-df1c-4efd-8020-67d146a850e0},14";

    // PKEY_Device_DeviceDesc — 짧은 설명 (예: "스피커") — 폴백용
    private const string DeviceDesc = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

    private const string Unknown = "(이름 없음)";

    public static string Read(string endpointId)
    {
        using RegistryKey? props = Registry.LocalMachine.OpenSubKey(MMDevicePaths.Properties(endpointId));
        if (props is null)
            return Unknown;

        return ReadString(props, FriendlyName)
            ?? ReadString(props, DeviceDesc)
            ?? Unknown;
    }

    private static string? ReadString(RegistryKey props, string valueName)
    {
        return props.GetValue(valueName) switch
        {
            string s when s.Trim().Length > 0 => s.Trim(),
            byte[] b => DecodeUtf16(b),
            _ => null,
        };
    }

    /// <summary>
    /// 일부 시스템은 이름을 직렬화 PROPVARIANT(VT_LPWSTR) REG_BINARY로 저장한다.
    /// 앞 4바이트(타입+예약)를 건너뛰고 UTF-16LE로 best-effort 디코드한다.
    /// </summary>
    private static string? DecodeUtf16(byte[] bytes)
    {
        if (bytes.Length <= 4)
            return null;

        string s = Encoding.Unicode.GetString(bytes, 4, bytes.Length - 4);
        int nul = s.IndexOf('\0');
        if (nul >= 0)
            s = s[..nul];
        s = s.Trim();
        return s.Length > 0 ? s : null;
    }
}
