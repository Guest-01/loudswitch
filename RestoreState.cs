using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Loudswitch;

/// <summary>
/// "앱이 바꿔 놓은 평준화를 나중에 원래대로 되돌려야 하는 장치" 목록. <c>%APPDATA%\Loudswitch\state.json</c>.
///
/// 앱이 대상 프로그램 때문에 평준화를 <b>직접 켰을 때만</b> 기록한다(원래 꺼져 있던 장치).
/// 사용자가 이미 켜 두었던 장치는 기록하지 않으므로, 대상 프로그램이 끝나도 그대로 켜져 있다.
/// 파일로 남겨 두기 때문에 앱이 비정상 종료돼도 다음 실행 때 되돌릴 수 있다.
/// 사용자 설정(<see cref="Config"/>)과 달리 앱이 관리하는 내부 상태라 파일을 분리했다.
/// </summary>
internal sealed class RestoreState
{
    /// <summary>엔드포인트 ID → 앱이 바꾸기 전의 원래 평준화 상태(true = 켜짐).</summary>
    public Dictionary<string, bool> Pending { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath => Path.Combine(Config.Dir, "state.json");

    /// <summary>읽는다. 없거나 손상됐으면 빈 상태.</summary>
    public static RestoreState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                RestoreState? s = JsonSerializer.Deserialize(File.ReadAllText(FilePath), RestoreStateJsonContext.Default.RestoreState);
                if (s is not null)
                {
                    // 역직렬화된 사전은 대소문자를 구분하므로 비교자를 다시 맞춘다.
                    s.Pending = new Dictionary<string, bool>(s.Pending, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch
        {
            // 손상된 상태 파일은 무시(되돌릴 기록을 잃을 뿐, 동작에는 지장 없음)
        }
        return new RestoreState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Config.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, RestoreStateJsonContext.Default.RestoreState));
        }
        catch
        {
            // 저장 실패는 치명적이지 않다(이번 실행 중에는 메모리의 기록으로 되돌린다).
        }
    }
}

/// <summary><see cref="RestoreState"/> JSON 직렬화의 소스 생성 컨텍스트(NativeAOT/트리밍 안전).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RestoreState))]
internal partial class RestoreStateJsonContext : JsonSerializerContext;
