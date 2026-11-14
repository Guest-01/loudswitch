using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Loudswitch;

/// <summary>
/// 트레이 앱 설정. <c>%APPDATA%\Loudswitch\config.json</c>에 저장된다.
/// 트레이 메뉴로 바꾸거나 파일을 직접 편집한다.
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

    /// <summary>
    /// 대상 출력 장치의 엔드포인트 ID(불투명한 문자열, 그대로 저장·비교). <c>null</c>이면 기본 출력 장치(권장 기본값).
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// 예전 형식(엔드포인트 ID 끝의 GUID만 저장). 읽을 때 <see cref="DeviceId"/>로 옮기고 지운다.
    /// 옮기지 못했으면(장치를 찾지 못함) 남겨 두고 다음 실행 때 다시 시도한다.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceGuid { get; set; }

    /// <summary>
    /// 앱 종료·설정 변경·기본 장치 변경 때도, 앱이 켜 두었던 평준화를 <b>원래 상태로</b> 되돌릴지.
    /// (대상 프로그램이 끝날 때는 이 값과 무관하게 항상 되돌린다.) false면 그때 기록만 지우고 지금 상태로 둔다.
    /// JSON 이름은 기존 설정 파일과의 호환을 위해 예전 이름(RestoreOffOnDisable)을 유지한다.
    /// </summary>
    [JsonPropertyName("RestoreOffOnDisable")]
    public bool RestoreOnExit { get; set; } = true;

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
                    cfg.MigrateLegacyDevice();
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

    private void MigrateLegacyDevice()
    {
        if (DeviceId is not null || string.IsNullOrWhiteSpace(DeviceGuid))
            return;

        string? id = Audio.EndpointResolver.MigrateLegacyGuid(DeviceGuid);
        if (id is null)
            return; // 이번엔 못 찾음 → 기본 장치로 동작, 다음 실행 때 재시도

        DeviceId = id;
        DeviceGuid = null;
        try
        {
            Save();
        }
        catch
        {
            // 저장 실패해도 이번 실행에는 메모리 값으로 동작
        }
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
