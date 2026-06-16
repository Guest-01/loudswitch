using Loudswitch.Interop;

namespace Loudswitch.Audio;

/// <summary>
/// 장치 GUID(또는 전체 엔드포인트 ID)를 IPolicyConfig가 요구하는 전체 엔드포인트 ID로 해석한다.
/// 콘솔(set/toggle/auto)·트레이가 공유한다.
/// </summary>
internal static class EndpointResolver
{
    /// <summary>
    /// <paramref name="deviceGuidOrId"/>가 null이면 기본 출력 장치를 반환한다.
    /// 전체 엔드포인트 ID나 <c>{GUID}</c>(중괄호 유무 무관) 모두 허용. 못 찾으면 null.
    /// </summary>
    public static string? Resolve(string? deviceGuidOrId)
    {
        if (deviceGuidOrId is null)
            return Default();

        string norm = MMDevicePaths.NormalizeGuid(deviceGuidOrId);

        IReadOnlyList<string> ids;
        try
        {
            ids = CoreAudio.GetRenderEndpointIds(activeOnly: false);
        }
        catch
        {
            return null;
        }

        foreach (string id in ids)
        {
            if (string.Equals(id, deviceGuidOrId, StringComparison.OrdinalIgnoreCase))
                return id;
            if (string.Equals(MMDevicePaths.DeviceGuid(id), norm, StringComparison.OrdinalIgnoreCase))
                return id;
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
