using System.IO;
using System.Text.Json;

namespace Loudswitch;

/// <summary>
/// 트레이 앱 설정. <c>%APPDATA%\Loudswitch\config.json</c>에 저장된다.
/// (편집 UI는 이후 슬라이스 — 지금은 파일을 직접 편집)
/// </summary>
internal sealed class Config
{
    /// <summary>감시할 프로세스 이름(확장자 무관, 예: "notepad").</summary>
    public string ProcessName { get; set; } = "notepad";

    /// <summary>
    /// 대상 엔드포인트 장치 GUID. <c>null</c>이면 기본 출력 장치.
    /// (개발 기본값은 이 PC의 테스트베드 — 실사용 시 이 파일을 편집)
    /// </summary>
    public string? DeviceGuid { get; set; } = "{3ecd2d07-4d4f-480e-9553-b03a2947a222}";

    /// <summary>프로세스 폴링 간격(ms). 최소 200.</summary>
    public int PollingIntervalMs { get; set; } = 1500;

    /// <summary>비활성화/종료 시 Loudness를 OFF로 복원할지.</summary>
    public bool RestoreOffOnDisable { get; set; } = true;

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
                Config? cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
                if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.ProcessName))
                    return cfg;
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
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
