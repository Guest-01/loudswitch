namespace Loudswitch.Audio;

/// <summary>
/// 엔드포인트 ID로부터 MMDevices 레지스트리 경로를 구성한다.
/// 레지스트리의 장치 키 이름은 엔드포인트 ID 끝의 <c>{GUID}</c> 부분이다.
/// </summary>
internal static class MMDevicePaths
{
    private const string RenderRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

    /// <summary>
    /// <c>{0.0.0.00000000}.{GUID}</c> 형식의 엔드포인트 ID에서 마지막 중괄호 GUID를 추출한다.
    /// </summary>
    public static string DeviceGuid(string endpointId)
    {
        int lastBrace = endpointId.LastIndexOf('{');
        if (lastBrace < 0)
            throw new FormatException($"예상치 못한 엔드포인트 ID 형식: {endpointId}");
        return endpointId[lastBrace..];
    }

    /// <summary>음향 효과(APO) 설정 저장소. Loudness EQ 키가 여기 있다.</summary>
    public static string FxProperties(string endpointId) =>
        $@"{RenderRoot}\{DeviceGuid(endpointId)}\FxProperties";

    /// <summary>장치 메타데이터 저장소. friendly name 등이 여기 있다.</summary>
    public static string Properties(string endpointId) =>
        $@"{RenderRoot}\{DeviceGuid(endpointId)}\Properties";

    /// <summary>중괄호 보정: "xxxx" → "{xxxx}", 이미 중괄호면 그대로.</summary>
    public static string NormalizeGuid(string s)
    {
        s = s.Trim();
        return s.StartsWith('{') ? s : "{" + s.Trim('{', '}') + "}";
    }
}
