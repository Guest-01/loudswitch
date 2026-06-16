using System.Text;
using Loudswitch.Audio;
using Loudswitch.Detection;
using Loudswitch.Interop;

namespace Loudswitch;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        TrySetUtf8Console();

        if (args.Length == 0)
            return ListDevices();

        return args[0].ToLowerInvariant() switch
        {
            "set" => RunSet(args),
            "toggle" => RunToggle(args),
            "watch" => RunWatch(args),
            "auto" => RunAuto(args),
            _ => Usage(),
        };
    }

    // ---------------------------------------------------------------- 슬라이스 1: 나열

    private static int ListDevices()
    {
        Console.WriteLine("Loudswitch — Loudness EQ 상태 읽기 (읽기 전용)\n");

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
        Console.WriteLine($"    Loudness EQ: {DescribeState(report)}");
        Console.WriteLine();
    }

    private static void PrintSummary(int total, int supported)
    {
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"요약: 활성 출력 {total}개 중 Loudness EQ 키 노출 {supported}개.");
    }

    // ---------------------------------------------------------------- 슬라이스 2: 쓰기/토글

    private static int RunSet(string[] args)
    {
        // set <guid|endpointId> on|off
        if (args.Length < 3)
            return Usage();

        string mode = args[2].ToLowerInvariant();
        if (mode != "on" && mode != "off")
            return Usage();

        string? endpointId = ResolveEndpointId(args[1]);
        if (endpointId is null)
            return DeviceNotFound(args[1]);

        return ApplyAndReport(endpointId, on: mode == "on");
    }

    private static int RunToggle(string[] args)
    {
        // toggle <guid|endpointId>
        if (args.Length < 2)
            return Usage();

        string? endpointId = ResolveEndpointId(args[1]);
        if (endpointId is null)
            return DeviceNotFound(args[1]);

        LoudnessEqualizationSetting.Report current = LoudnessEqualizationSetting.Read(endpointId);
        bool newOn = current.State != LoudnessEqualizationSetting.State.On; // 켜져 있으면 끄고, 아니면 켠다
        return ApplyAndReport(endpointId, newOn);
    }

    /// <summary>
    /// 쓰기 전/후 레지스트리 값을 읽어 IPolicyConfig 쓰기 결과를 검증·보고한다.
    /// (소리/GUI 확인은 descope — API 쓰기 경로·값 형식·HRESULT만 증명)
    /// </summary>
    private static int ApplyAndReport(string endpointId, bool on)
    {
        LoudnessEqualizationSetting.Report before = LoudnessEqualizationSetting.Read(endpointId);
        int hr = PolicyConfig.SetLoudness(endpointId, on);
        LoudnessEqualizationSetting.Report after = LoudnessEqualizationSetting.Read(endpointId);

        Console.WriteLine($"대상  : {endpointId}");
        Console.WriteLine($"목표  : {(on ? "ON" : "OFF")}");
        Console.WriteLine($"HRESULT: 0x{hr:X8} {(hr == 0 ? "(S_OK)" : "(실패)")}");
        Console.WriteLine($"before: {DescribeState(before)}");
        Console.WriteLine($"after : {DescribeState(after)}");
        Console.WriteLine();

        if (hr != 0)
        {
            Console.WriteLine("→ SetPropertyValue 실패. 이 엔드포인트가 IPolicyConfig 쓰기를 거부합니다.");
            return 1;
        }

        bool changedAsExpected = on
            ? after.State == LoudnessEqualizationSetting.State.On
            : after.State == LoudnessEqualizationSetting.State.NotOn;

        if (!changedAsExpected)
        {
            Console.WriteLine("→ HRESULT는 S_OK인데 레지스트리가 기대대로 안 바뀌었습니다 (중요한 발견).");
            Console.WriteLine("  잠시 후 `dotnet run`으로 다시 확인하거나, 이 엔드포인트가 키를 무시하는지 점검 필요.");
            return 1;
        }

        Console.WriteLine(on
            ? "→ ON 성공. after가 알려진 ON 패턴과 일치하면 PROPVARIANT 구성이 네이티브와 동일합니다."
            : "→ OFF 성공. 위 after의 raw 바이트가 '실제 OFF 직렬화 형식'입니다 (미해결 질문 해소).");
        return 0;
    }

    // ---------------------------------------------------------------- 슬라이스 3: 감지(로그만)

    private static int RunWatch(string[] args)
    {
        // watch <processName> [intervalMs]
        if (args.Length < 2)
            return Usage();

        string name = args[1];
        int interval = 1500;
        if (args.Length >= 3 && int.TryParse(args[2], out int n) && n >= 200)
            interval = n;

        using var watcher = new ProcessWatcher(name, interval);
        watcher.Started += count => Log($"시작 감지: '{name}' (인스턴스 {count}개)");
        watcher.Stopped += () => Log($"종료 감지: '{name}'");

        Console.WriteLine($"'{name}' 감시 시작 (폴링 {interval}ms). Ctrl+C로 종료.\n");
        watcher.Start();

        using var exit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // 즉시 강제 종료 대신 정상 종료 경로로
            exit.Set();
        };
        exit.Wait();

        Console.WriteLine("\n감시 종료.");
        return 0;
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    // ---------------------------------------------------------------- 슬라이스 4: 통합(감지→토글)

    private static int RunAuto(string[] args)
    {
        // auto <processName> [deviceGuid]   (deviceGuid 생략 시 기본 출력 장치)
        if (args.Length < 2)
            return Usage();

        string name = args[1];

        string? endpointId;
        if (args.Length >= 3)
        {
            endpointId = ResolveEndpointId(args[2]);
            if (endpointId is null)
                return DeviceNotFound(args[2]);
        }
        else
        {
            endpointId = TryGetDefaultId();
            if (endpointId is null)
            {
                Console.Error.WriteLine("기본 출력 장치를 찾지 못했습니다.");
                return 1;
            }
        }

        LoudnessEqualizationSetting.Report initial = LoudnessEqualizationSetting.Read(endpointId);
        Console.WriteLine($"대상 엔드포인트: {endpointId}");
        Console.WriteLine($"현재 Loudness : {DescribeState(initial)}");
        if (initial.State == LoudnessEqualizationSetting.State.Unsupported)
            Console.WriteLine("주의: 이 장치는 Loudness EQ 미노출 → 토글은 skip되고 감지 로그만 출력됩니다.");
        Console.WriteLine($"'{name}' 감시 시작. 시작=ON / 종료=OFF. Ctrl+C로 종료.\n");

        using var watcher = new ProcessWatcher(name);
        watcher.Started += count =>
        {
            Log($"시작 감지: '{name}' (인스턴스 {count}개) → ON");
            ApplyAuto(endpointId, on: true);
        };
        watcher.Stopped += () =>
        {
            Log($"종료 감지: '{name}' → OFF");
            ApplyAuto(endpointId, on: false);
        };
        watcher.Start();

        using var exit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exit.Set();
        };
        exit.Wait();

        Console.WriteLine("\n감시 종료.");
        return 0;
    }

    /// <summary>
    /// 토글 직전에 키 존재를 다시 확인해 미지원 장치엔 쓰지 않는다(force-create 금지 원칙).
    /// </summary>
    private static void ApplyAuto(string endpointId, bool on)
    {
        LoudnessEqualizationSetting.Report state = LoudnessEqualizationSetting.Read(endpointId);
        if (state.State == LoudnessEqualizationSetting.State.Unsupported)
        {
            Log("  → 미지원 (키 없음) → 토글 skip (force-create 안 함)");
            return;
        }

        int hr = PolicyConfig.SetLoudness(endpointId, on);
        LoudnessEqualizationSetting.Report after = LoudnessEqualizationSetting.Read(endpointId);
        Log($"  → HRESULT 0x{hr:X8} ({(hr == 0 ? "S_OK" : "실패")}), 상태: {DescribeState(after)}");
    }

    // ---------------------------------------------------------------- 공용

    /// <summary>
    /// 인자가 전체 엔드포인트 ID면 그대로, 장치 {GUID}면 열거해서 전체 엔드포인트 ID로 변환한다.
    /// IPolicyConfig는 전체 엔드포인트 ID를 요구하므로 변환이 필요하다.
    /// </summary>
    private static string? ResolveEndpointId(string arg)
    {
        string normGuid = NormalizeGuid(arg);

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
            if (string.Equals(id, arg, StringComparison.OrdinalIgnoreCase))
                return id;
            if (string.Equals(MMDevicePaths.DeviceGuid(id), normGuid, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return null;
    }

    private static string NormalizeGuid(string s)
    {
        s = s.Trim();
        if (!s.StartsWith('{'))
            s = "{" + s.Trim('{', '}') + "}";
        return s;
    }

    private static string DescribeState(LoudnessEqualizationSetting.Report r) => r.State switch
    {
        LoudnessEqualizationSetting.State.Unsupported => "[미지원] (FxProperties 또는 키 없음)",
        LoudnessEqualizationSetting.State.On => $"[켜짐] {Hex(r.RawValue!)}",
        LoudnessEqualizationSetting.State.NotOn => $"[ON 아님] {Hex(r.RawValue!)}",
        _ => "?",
    };

    private static int DeviceNotFound(string arg)
    {
        Console.Error.WriteLine($"장치를 찾지 못했습니다: {arg}");
        Console.Error.WriteLine("  `dotnet run`으로 GUID를 확인하세요.");
        return 1;
    }

    private static int Usage()
    {
        Console.WriteLine("사용법:");
        Console.WriteLine("  dotnet run                         활성 출력 장치 + Loudness 상태 나열");
        Console.WriteLine("  dotnet run -- set <guid> on|off    IPolicyConfig로 Loudness 쓰기");
        Console.WriteLine("  dotnet run -- toggle <guid>        현재 상태의 반대로 토글");
        Console.WriteLine("  dotnet run -- watch <name> [ms]    프로세스 시작/종료 감지 (로그만)");
        Console.WriteLine("  dotnet run -- auto <name> [guid]   프로세스 감지 → Loudness 토글 (guid 없으면 기본 장치)");
        return 2;
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
