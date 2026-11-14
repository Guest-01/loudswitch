using Loudswitch.Interop;
using Microsoft.Win32;

namespace Loudswitch.Audio;

/// <summary>
/// 출력 엔드포인트의 Loudness Equalization 상태와 "어느 경로로 다뤄야 하는지"를 판정한다(읽기 전용).
///
/// 판정 순서 (2026-11-14 재설계):
/// <list type="number">
///   <item><b>공식 API</b>(Windows 11 효과 설정 저장소, User → Default 층)에 값이 있으면 그것을 쓴다.
///         Windows 설정 화면이 읽고 쓰는 곳과 같다.</item>
///   <item>없으면 <b>옛 방식</b> 위치(FxProperties 바로 아래)에 값이 있는지 본다. 공식 저장소를 쓰지 않는
///         옛 드라이버, Windows 10, 개발용 테스트베드가 여기에 해당한다.</item>
///   <item>둘 다 값이 없지만 장치에 <b>Microsoft 기본 효과 모듈</b>이 붙어 있으면 "지원, 꺼짐"으로 본다.
///         평준화는 기본이 꺼짐이고, 한 번도 바꾸지 않은 장치엔 값이 저장돼 있지 않다.</item>
///   <item>그 외(제조사 전용 효과 모듈 등)는 미지원 — 건드리지 않는다.</item>
/// </list>
/// </summary>
internal static class LoudnessEqualizationSetting
{
    // MFPKEY_CORR_LOUDNESS_EQUALIZATION_ON ({GUID},PID) — 옛 방식 레지스트리 값 이름
    private const string PropertyKey = "{fc52a749-4be9-4510-896e-966ba6525980},3";

    // 옛 방식 위치에 저장된 "켜짐" 직렬화 PROPVARIANT (VT_BOOL = VARIANT_TRUE, 0xFFFF).
    private static readonly byte[] KnownOnValue =
        [0x0b, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00];

    // 효과 모듈 체인: PKEY_FX_PreMixEffectClsid(,1) / PostMix(,2) / UserInterface(,3) /
    // StreamEffect(,5) / ModeEffect(,6) / EndpointEffect(,7) — {d04e05a6-...},n
    private const string FxChainPrefix = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},";

    // 평준화를 제공하는 Microsoft 기본 효과 모듈 CLSID.
    private static readonly HashSet<string> MicrosoftLoudnessApos = new(StringComparer.OrdinalIgnoreCase)
    {
        "{C9453E73-8C5C-4463-9984-AF8BAB2F5447}", // WM LFX APO (WMALFXGFXDSP.dll)
        "{13AB3EBD-137E-4903-9D89-60BE8277FD17}", // WM GFX APO (WMALFXGFXDSP.dll)
        "{62dc1a93-ae24-464c-a43e-452f824c4250}", // MS 기본 stream effect (테스트베드가 쓰는 값)
        "{637c490d-eee3-4c0a-973f-371958802da2}", // MS 기본 mode effect (테스트베드가 쓰는 값)
    };

    public enum State
    {
        /// <summary>평준화를 다룰 수 없는 장치 → skip 대상.</summary>
        Unsupported,

        On,

        Off,
    }

    /// <summary>쓰기에 사용할 경로.</summary>
    public enum Backend
    {
        None,

        /// <summary>Windows 11 공식 API(효과 설정 저장소 User 층).</summary>
        Official,

        /// <summary>옛 방식(FxProperties 바로 아래 + 비공개 IPolicyConfig).</summary>
        Legacy,
    }

    /// <param name="Detail">사람이 읽을 근거(어느 층/위치에서 읽었는지, 옛 방식이면 raw 바이트).</param>
    public sealed record Report(State State, Backend Backend, string Detail);

    public static Report Read(string endpointId)
    {
        // 1) 공식 API
        EffectsPropertyStore.ReadResult official = default;
        try
        {
            official = EffectsPropertyStore.ReadLoudness(endpointId);
        }
        catch
        {
            // 공식 경로 실패는 치명적이지 않다 — 옛 방식으로 계속.
        }

        if (official.Value is bool v)
            return new Report(v ? State.On : State.Off, Backend.Official, $"공식 API · {official.Layer} 층");

        // 2) 옛 방식 위치
        string? fxPath = MMDevicePaths.FxProperties(endpointId);
        using RegistryKey? fx = fxPath is null ? null : Registry.LocalMachine.OpenSubKey(fxPath);
        if (fx?.GetValue(PropertyKey) is byte[] raw)
        {
            State s = raw.AsSpan().SequenceEqual(KnownOnValue) ? State.On : State.Off;
            return new Report(s, Backend.Legacy, $"옛 방식 · {Hex(raw)}");
        }

        // 3) 값은 없지만 Microsoft 기본 효과 모듈이 붙어 있음 → 지원, 꺼짐(한 번도 바꾸지 않음)
        if (fx is not null && UsesMicrosoftLoudnessApo(fx))
        {
            return official.Available
                ? new Report(State.Off, Backend.Official, "공식 API · 값 없음(기본값 꺼짐)")
                : new Report(State.Off, Backend.Legacy, "옛 방식 · 값 없음(기본값 꺼짐)");
        }

        // 4) 미지원
        return new Report(State.Unsupported, Backend.None,
            fx is null ? "효과 설정 저장소 없음" : "Microsoft 기본 효과 모듈 아님");
    }

    private static bool UsesMicrosoftLoudnessApo(RegistryKey fx)
    {
        foreach (string name in fx.GetValueNames())
        {
            if (name.StartsWith(FxChainPrefix, StringComparison.OrdinalIgnoreCase) &&
                fx.GetValue(name) is string clsid &&
                MicrosoftLoudnessApos.Contains(clsid.Trim()))
            {
                return true;
            }
        }
        return false;
    }

    private static string Hex(byte[] bytes) =>
        "hex:" + string.Join(",", bytes.Select(b => b.ToString("x2")));
}
