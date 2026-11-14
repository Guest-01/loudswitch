using Loudswitch.Interop;

namespace Loudswitch.Audio;

/// <summary>
/// Loudness EQ 토글의 단일 진입점. 읽기 판정(<see cref="LoudnessEqualizationSetting"/>)이 고른 경로로만 쓴다:
/// 공식 API로 읽힌 장치는 공식 API로, 옛 방식 장치는 IPolicyConfig로, 미지원 장치는 건드리지 않는다.
/// 콘솔(set/toggle/auto)·트레이가 공통으로 사용한다.
/// </summary>
internal static class LoudnessController
{
    public enum ApplyResult
    {
        /// <summary>값을 바꿨다(HRESULT S_OK).</summary>
        Set,

        /// <summary>이미 목표 상태라 쓰지 않았다(불필요한 효과 재로드 방지).</summary>
        Unchanged,

        /// <summary>미지원 장치라 건드리지 않았다.</summary>
        SkippedUnsupported,

        /// <summary>쓰기를 시도했으나 실패했다.</summary>
        Failed,
    }

    public static ApplyResult Apply(string endpointId, bool on) => Apply(endpointId, on, out _);

    /// <param name="hr">쓰기를 시도했다면 그 HRESULT, 아니면 0.</param>
    public static ApplyResult Apply(string endpointId, bool on, out int hr)
    {
        hr = 0;
        LoudnessEqualizationSetting.Report current = LoudnessEqualizationSetting.Read(endpointId);

        if (current.State == LoudnessEqualizationSetting.State.Unsupported)
            return ApplyResult.SkippedUnsupported;

        bool isOn = current.State == LoudnessEqualizationSetting.State.On;
        if (isOn == on)
            return ApplyResult.Unchanged;

        hr = current.Backend switch
        {
            LoudnessEqualizationSetting.Backend.Official => EffectsPropertyStore.WriteLoudness(endpointId, on),
            LoudnessEqualizationSetting.Backend.Legacy => PolicyConfig.SetLoudness(endpointId, on),
            _ => unchecked((int)0x80004005), // E_FAIL (도달 불가)
        };
        return hr == 0 ? ApplyResult.Set : ApplyResult.Failed;
    }
}
