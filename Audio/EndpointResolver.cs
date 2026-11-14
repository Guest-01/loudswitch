using Loudswitch.Interop;

namespace Loudswitch.Audio;

/// <summary>
/// 설정·명령줄의 장치 지정을 엔드포인트 ID로 해석한다. 엔드포인트 ID는 불투명한 문자열로만 다룬다
/// (Microsoft 문서: 파싱 금지) — 저장도 비교도 ID 전체로 한다.
/// </summary>
internal static class EndpointResolver
{
    /// <summary>
    /// <paramref name="endpointId"/>가 null이면 기본 출력 장치를, 아니면 그 ID의 장치가 있는지 확인해 돌려준다.
    /// 장치를 찾지 못하면 null.
    /// </summary>
    public static string? Resolve(string? endpointId)
    {
        if (endpointId is null)
            return Default();

        try
        {
            return CoreAudio.GetDevice(endpointId) is not null ? endpointId : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 개발용 명령의 장치 인자: <c>list</c>에 나온 번호(1부터) 또는 엔드포인트 ID 전체. 못 찾으면 null.
    /// </summary>
    public static string? ResolveArgument(string arg)
    {
        if (int.TryParse(arg, out int index))
        {
            try
            {
                IReadOnlyList<string> active = CoreAudio.GetRenderEndpointIds(activeOnly: true);
                return index >= 1 && index <= active.Count ? active[index - 1] : null;
            }
            catch
            {
                return null;
            }
        }
        return Resolve(arg);
    }

    /// <summary>
    /// 예전 설정 파일의 장치 GUID(엔드포인트 ID의 끝부분만 저장하던 방식)를 엔드포인트 ID로 바꾼다.
    /// 설정 이전(마이그레이션) 한 번에만 쓰며, 그 때문에 이 함수만 예외적으로 ID의 끝부분을 비교한다.
    /// </summary>
    public static string? MigrateLegacyGuid(string legacyGuid)
    {
        string guid = legacyGuid.Trim().Trim('{', '}');
        if (guid.Length == 0)
            return null;
        try
        {
            foreach (string id in CoreAudio.GetRenderEndpointIds(activeOnly: false))
            {
                if (id.EndsWith("{" + guid + "}", StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        }
        catch
        {
            // 열거 실패 → 이전 불가(기본 장치로 동작)
        }
        return null;
    }

    /// <summary>기본 멀티미디어 출력 엔드포인트 ID. 없으면 null.</summary>
    public static string? Default()
    {
        try
        {
            return CoreAudio.GetDefaultRenderEndpointId();
        }
        catch
        {
            return null;
        }
    }
}
