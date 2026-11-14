namespace Loudswitch.Audio;

/// <summary>
/// <b>옛 방식(비공개) 경로 전용</b>: 엔드포인트의 MMDevices 레지스트리 위치를 만든다.
///
/// 이 레지스트리 구조와, 레지스트리 키 이름이 엔드포인트 ID 끝의 <c>{GUID}</c>라는 사실은 둘 다 문서화되지 않았다.
/// 공식 API가 없는 두 가지 일(효과 모듈 체인 확인, 옛 드라이버의 평준화 값)에만 쓰고, 그 밖의 곳에서는
/// 엔드포인트 ID를 불투명한 문자열로 다룬다. ID 형식이 예상과 다르면 null을 돌려 해당 경로를 건너뛴다.
/// </summary>
internal static class MMDevicePaths
{
    private const string RenderRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

    /// <summary>음향 효과(APO) 설정 저장소(옛 방식 위치). 형식을 알 수 없는 ID면 null.</summary>
    public static string? FxProperties(string endpointId)
    {
        int lastBrace = endpointId.LastIndexOf('{');
        if (lastBrace < 0 || !endpointId.EndsWith('}'))
            return null;
        return $@"{RenderRoot}\{endpointId[lastBrace..]}\FxProperties";
    }
}
