using Microsoft.Win32;

namespace Loudswitch.Audio;

/// <summary>
/// 출력 엔드포인트의 Loudness Equalization 설정을 레지스트리에서 <b>읽어 보고만</b> 한다.
///
/// 슬라이스 1의 목적은 두 가지다:
/// <list type="number">
///   <item>이 장치/드라이버가 Loudness EQ 키를 노출하는가? (go/no-go)</item>
///   <item>실제 ON/OFF 직렬화 PROPVARIANT 바이트 패턴은 무엇인가? (슬라이스 2의 read→flip→write 입력)</item>
/// </list>
/// 끄기 값 형식은 아직 미확정이므로 <b>추측하지 않는다</b>. 출처에서 확인된 ON 패턴과
/// 정확히 일치할 때만 ON으로 단정하고, 그 외에는 raw 바이트를 그대로 보고한다.
/// </summary>
internal static class LoudnessEqualizationSetting
{
    // MFPKEY_CORR_LOUDNESS_EQUALIZATION_ON ({GUID},PID)
    private const string PropertyKey = "{fc52a749-4be9-4510-896e-966ba6525980},3";

    // 출처에서 확인된 "켜짐" 직렬화 PROPVARIANT (VT_BOOL = VARIANT_TRUE, 0xFFFF).
    private static readonly byte[] KnownOnValue =
        [0x0b, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00];

    public enum State
    {
        /// <summary>FxProperties 저장소 또는 해당 키가 없음 → 미지원 장치, skip 대상.</summary>
        Unsupported,

        /// <summary>출처에서 확인된 ON 패턴과 정확히 일치.</summary>
        On,

        /// <summary>키는 있으나 알려진 ON 값이 아님 (꺼짐으로 추정하되 단정하지 않음).</summary>
        NotOn,
    }

    public sealed record Report(State State, byte[]? RawValue, string RegistryPath);

    public static Report Read(string endpointId)
    {
        string fxPath = MMDevicePaths.FxProperties(endpointId);
        string fullPath = $@"HKLM\{fxPath}";

        using RegistryKey? fx = Registry.LocalMachine.OpenSubKey(fxPath);
        if (fx is null)
            return new Report(State.Unsupported, null, fullPath);

        if (fx.GetValue(PropertyKey) is not byte[] raw)
            return new Report(State.Unsupported, null, fullPath);

        State state = raw.AsSpan().SequenceEqual(KnownOnValue) ? State.On : State.NotOn;
        return new Report(state, raw, fullPath);
    }
}
