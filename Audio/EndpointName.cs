using Loudswitch.Interop;

namespace Loudswitch.Audio;

/// <summary>
/// 엔드포인트의 사람이 읽을 수 있는 이름. 공식 장치 속성(<c>PKEY_Device_FriendlyName</c>)을 읽으며,
/// Windows 사운드 설정에 보이는 이름과 같다(예: "헤드폰 (RØDE NT-USB Mini)").
/// 이름은 보조 정보이므로 못 읽어도 치명적이지 않고 "(이름 없음)"으로 대체한다.
/// </summary>
internal static class EndpointName
{
    private const string Unknown = "(이름 없음)";

    public static string Read(string endpointId)
    {
        try
        {
            string? name = CoreAudio.GetFriendlyName(endpointId)?.Trim();
            return string.IsNullOrEmpty(name) ? Unknown : name;
        }
        catch
        {
            return Unknown;
        }
    }
}
