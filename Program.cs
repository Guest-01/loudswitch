using System.Text;
using Loudswitch.Audio;
using Loudswitch.Interop;

namespace Loudswitch;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        TrySetUtf8Console();
        Console.WriteLine("Loudswitch — 슬라이스 1: Loudness EQ 상태 읽기 (읽기 전용)\n");

        IReadOnlyList<string> endpointIds;
        try
        {
            endpointIds = CoreAudio.GetRenderEndpointIds(activeOnly: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[오류] 출력 장치 열거 실패: {ex.Message}");
            return 1;
        }

        if (endpointIds.Count == 0)
        {
            Console.WriteLine("활성 출력 장치가 없습니다.");
            return 0;
        }

        string? defaultId = TryGetDefaultId();
        Console.WriteLine($"활성 출력 장치 {endpointIds.Count}개:\n");

        int index = 1;
        int supported = 0;
        foreach (string id in endpointIds)
        {
            LoudnessEqualizationSetting.Report report = LoudnessEqualizationSetting.Read(id);
            if (report.State != LoudnessEqualizationSetting.State.Unsupported)
                supported++;

            PrintDevice(index++, EndpointName.Read(id), id == defaultId, id, report);
        }

        PrintSummary(endpointIds.Count, supported);
        return 0;
    }

    private static void PrintDevice(int index, string name, bool isDefault, string endpointId,
        LoudnessEqualizationSetting.Report report)
    {
        string defaultTag = isDefault ? "  (기본 장치)" : "";
        Console.WriteLine($"[{index}] {name}{defaultTag}");
        Console.WriteLine($"    엔드포인트: {endpointId}");

        switch (report.State)
        {
            case LoudnessEqualizationSetting.State.Unsupported:
                Console.WriteLine("    Loudness EQ: [미지원]  (FxProperties 또는 키 없음 → skip)");
                break;

            case LoudnessEqualizationSetting.State.On:
                Console.WriteLine("    Loudness EQ: [켜짐]    (알려진 ON 패턴과 일치)");
                Console.WriteLine($"      raw: {Hex(report.RawValue!)}");
                break;

            case LoudnessEqualizationSetting.State.NotOn:
                Console.WriteLine("    Loudness EQ: [ON 아님] (꺼짐 추정 — 단정 안 함)");
                Console.WriteLine($"      raw: {Hex(report.RawValue!)}");
                break;
        }

        Console.WriteLine();
    }

    private static void PrintSummary(int total, int supported)
    {
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"요약: 활성 출력 {total}개 중 Loudness EQ 키 노출 {supported}개.");
        if (supported == 0)
        {
            Console.WriteLine("→ 키를 노출하는 장치가 없습니다. 이 PC에서는 토글 대상이 없을 수 있습니다.");
            Console.WriteLine("  (비활성/미연결 장치까지 보려면 activeOnly=false로 다시 시도)");
        }
        else
        {
            Console.WriteLine("→ [ON 아님]/[켜짐] 장치를 GUI에서 켜고/끈 뒤 다시 실행해 raw 값을 각각 기록하세요.");
            Console.WriteLine("  (켠 값 vs 끈 값 = 슬라이스 2의 read→flip→write 입력)");
        }
    }

    private static string? TryGetDefaultId()
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

    private static string Hex(byte[] bytes) =>
        "hex:" + string.Join(",", bytes.Select(b => b.ToString("x2")));

    private static void TrySetUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 출력이 리다이렉트된 경우 등에서 실패할 수 있으나 치명적이지 않다.
        }
    }
}
