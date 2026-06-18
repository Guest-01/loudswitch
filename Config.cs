using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Loudswitch;

/// <summary>
/// 트레이 앱 설정. <c>%APPDATA%\Loudswitch\config.json</c>에 저장된다.
/// (편집 UI는 이후 슬라이스 — 지금은 파일을 직접 편집)
/// </summary>
internal sealed class Config
{
    /// <summary>프로세스 폴링 간격(ms) — 고정값(앱 전역 단일 출처). UI 노출 없음.</summary>
    public const int PollingIntervalMs = 1000;

    /// <summary>
    /// 감시할 프로세스 이름(확장자 무관, 예: "notepad"). 빈 문자열 = 아직 미설정
    /// (첫 실행 기본값). 미설정 상태에서는 감시/토글을 하지 않는다.
    /// </summary>
    public string ProcessName { get; set; } = "";

    /// <summary>대상 엔드포인트 장치 GUID. <c>null</c>이면 기본 출력 장치(권장 기본값).</summary>
    public string? DeviceGuid { get; set; }

    /// <summary>종료/장치 변경 시 Loudness를 OFF로 복원할지.</summary>
    public bool RestoreOffOnDisable { get; set; } = true;

    /// <summary>Loudness 토글 시 알림(토스트)을 표시할지.</summary>
    public bool NotifyOnToggle { get; set; } = true;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loudswitch");

    public static string FilePath => Path.Combine(Dir, "config.json");

    /// <summary>
    /// 설정을 읽는다. 없거나 손상됐으면 기본값으로 새로 만들고 <paramref name="created"/>를 true로.
    /// </summary>
    public static Config LoadOrCreateDefault(out bool created)
    {
        created = false;
        try
        {
            if (File.Exists(FilePath))
            {
                Config? cfg = JsonSerializer.Deserialize(File.ReadAllText(FilePath), ConfigJsonContext.Default.Config);
                if (cfg is not null)
                {
                    // 미설정(빈 ProcessName)도 유효한 상태다 — 손상 판정은 역직렬화 실패만.
                    return cfg;
                }
            }
        }
        catch
        {
            // 손상된 설정은 무시하고 기본값으로 재생성
        }

        var def = new Config();
        def.Save();
        created = true;
        return def;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, ConfigJsonContext.Default.Config));
    }
}

/// <summary>
/// <see cref="Config"/> JSON 직렬화의 소스 생성 컨텍스트. 리플렉션 기반 직렬화를 피해
/// NativeAOT/트리밍에서 안전하게 동작한다.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Config))]
internal partial class ConfigJsonContext : JsonSerializerContext;
