using Loudswitch.Interop;

namespace Loudswitch.Audio;

/// <summary>
/// Loudness EQ 토글의 단일 진입점. **force-create 금지 원칙을 한곳에서 강제**한다:
/// 키가 없는(미지원) 장치엔 절대 쓰지 않고, 지원 장치만 IPolicyConfig로 토글한다.
/// 콘솔(auto)·트레이가 공통으로 사용한다.
/// </summary>
internal static class LoudnessController
{
    public enum ApplyResult
    {
        /// <summary>IPolicyConfig로 정상 적용됨(S_OK).</summary>
        Set,

        /// <summary>키 미노출 장치라 건드리지 않음.</summary>
        SkippedUnsupported,

        /// <summary>호출은 했으나 HRESULT가 S_OK가 아님.</summary>
        Failed,
    }

    public static ApplyResult Apply(string endpointId, bool on)
    {
        LoudnessEqualizationSetting.Report state = LoudnessEqualizationSetting.Read(endpointId);
        if (state.State == LoudnessEqualizationSetting.State.Unsupported)
            return ApplyResult.SkippedUnsupported;

        int hr = PolicyConfig.SetLoudness(endpointId, on);
        return hr == 0 ? ApplyResult.Set : ApplyResult.Failed;
    }
}
