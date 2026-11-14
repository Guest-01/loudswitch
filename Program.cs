using System.Runtime.InteropServices;
using System.Text;
using Loudswitch.Audio;
using Loudswitch.Detection;
using Loudswitch.Interop;
using Loudswitch.Tray;

namespace Loudswitch;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
            return RunTray();

        // WinExe라 자체 콘솔이 없으므로, 터미널에서 dev 커맨드 실행 시 부모 콘솔에 붙여 출력한다.
        AttachConsole(ATTACH_PARENT_PROCESS);
        TrySetUtf8Console();
        return args[0].ToLowerInvariant() switch
        {
            "list" => ListDevices(),
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

        string? defaultId = EndpointResolver.Default();
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
        Console.WriteLine($"요약: 활성 출력 {total}개 중 평준화 지원 {supported}개.");
    }

    // ---------------------------------------------------------------- 슬라이스 2: 쓰기/토글

    private static int RunSet(string[] args)
    {
        // set <번호|endpointId> on|off
        if (args.Length < 3)
            return Usage();

        string mode = args[2].ToLowerInvariant();
        if (mode != "on" && mode != "off")
            return Usage();

        string? endpointId = EndpointResolver.ResolveArgument(args[1]);
        if (endpointId is null)
            return DeviceNotFound(args[1]);

        return ApplyAndReport(endpointId, on: mode == "on");
    }

    private static int RunToggle(string[] args)
    {
        // toggle <번호|endpointId>
        if (args.Length < 2)
            return Usage();

        string? endpointId = EndpointResolver.ResolveArgument(args[1]);
        if (endpointId is null)
            return DeviceNotFound(args[1]);

        LoudnessEqualizationSetting.Report current = LoudnessEqualizationSetting.Read(endpointId);
        bool newOn = current.State != LoudnessEqualizationSetting.State.On; // 켜져 있으면 끄고, 아니면 켠다
        return ApplyAndReport(endpointId, newOn);
    }

    /// <summary>
    /// 앱과 같은 경로(<see cref="LoudnessController"/>)로 쓰고, 쓰기 전/후 상태를 다시 읽어 검증·보고한다.
    /// 미지원 장치는 트레이와 똑같이 건드리지 않는다.
    /// </summary>
    private static int ApplyAndReport(string endpointId, bool on)
    {
        LoudnessEqualizationSetting.Report before = LoudnessEqualizationSetting.Read(endpointId);
        LoudnessController.ApplyResult result = LoudnessController.Apply(endpointId, on, out int hr);
        LoudnessEqualizationSetting.Report after = LoudnessEqualizationSetting.Read(endpointId);

        Console.WriteLine($"대상  : {endpointId}");
        Console.WriteLine($"목표  : {(on ? "ON" : "OFF")}");
        Console.WriteLine($"결과  : {result}{(result == LoudnessController.ApplyResult.Failed ? $" (HRESULT 0x{hr:X8})" : "")}");
        Console.WriteLine($"before: {DescribeState(before)}");
        Console.WriteLine($"after : {DescribeState(after)}");
        Console.WriteLine();

        switch (result)
        {
            case LoudnessController.ApplyResult.SkippedUnsupported:
                Console.WriteLine("→ 미지원 장치라 건드리지 않았습니다.");
                return 1;
            case LoudnessController.ApplyResult.Failed:
                Console.WriteLine($"→ 쓰기 실패 ({before.Backend} 경로).");
                return 1;
            case LoudnessController.ApplyResult.Unchanged:
                Console.WriteLine("→ 이미 목표 상태라 쓰지 않았습니다.");
                return 0;
        }

        bool changedAsExpected = after.State == (on ? LoudnessEqualizationSetting.State.On : LoudnessEqualizationSetting.State.Off);
        if (!changedAsExpected)
        {
            Console.WriteLine("→ 쓰기는 성공했는데 다시 읽은 상태가 기대와 다릅니다 (중요한 발견).");
            return 1;
        }

        Console.WriteLine($"→ {(on ? "ON" : "OFF")} 성공 ({after.Backend} 경로).");
        return 0;
    }

    // ---------------------------------------------------------------- 슬라이스 3: 감지(로그만)

    private static int RunWatch(string[] args)
    {
        // watch <processName> [intervalMs]
        if (args.Length < 2)
            return Usage();

        string name = args[1];
        int interval = Config.PollingIntervalMs;
        if (args.Length >= 3 && int.TryParse(args[2], out int n) && n >= 100)
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

        string? endpointId = args.Length >= 3 ? EndpointResolver.ResolveArgument(args[2]) : EndpointResolver.Default();
        if (endpointId is null)
        {
            Console.Error.WriteLine(args.Length >= 3
                ? $"장치를 찾지 못했습니다: {args[2]}"
                : "기본 출력 장치를 찾지 못했습니다.");
            return 1;
        }

        LoudnessEqualizationSetting.Report initial = LoudnessEqualizationSetting.Read(endpointId);
        Console.WriteLine($"대상 엔드포인트: {endpointId}");
        Console.WriteLine($"현재 Loudness : {DescribeState(initial)}");
        if (initial.State == LoudnessEqualizationSetting.State.Unsupported)
            Console.WriteLine("주의: 이 장치는 평준화 미지원 → 토글은 skip되고 감지 로그만 출력됩니다.");
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

    private static void ApplyAuto(string endpointId, bool on)
    {
        switch (LoudnessController.Apply(endpointId, on, out int hr))
        {
            case LoudnessController.ApplyResult.SkippedUnsupported:
                Log("  → 미지원 장치 → 토글 skip");
                break;
            case LoudnessController.ApplyResult.Failed:
                Log($"  → 쓰기 실패 (HRESULT 0x{hr:X8})");
                break;
            case LoudnessController.ApplyResult.Unchanged:
                Log("  → 이미 목표 상태 (쓰지 않음)");
                break;
            case LoudnessController.ApplyResult.Set:
                Log($"  → OK, 상태: {DescribeState(LoudnessEqualizationSetting.Read(endpointId))}");
                break;
        }
    }

    // ---------------------------------------------------------------- 슬라이스 5: 트레이

    private static int RunTray()
    {
        // 단일 인스턴스: 이미 실행 중이면 알리고 종료. (Mutex는 프로세스 생명주기 동안 보관)
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\Loudswitch.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Win32.MessageBoxW(0, "Loudswitch가 이미 실행 중입니다. 시스템 트레이를 확인하세요.",
                "Loudswitch", 0x40 /* MB_ICONINFORMATION */);
            return 0;
        }

        Config config = Config.LoadOrCreateDefault(out bool created);

        if (created)
        {
            Win32.MessageBoxW(0,
                $"설정 파일을 생성했습니다:\n{Config.FilePath}\n\n트레이 아이콘을 클릭해 적용할 프로그램·장치를 지정하세요.",
                "Loudswitch", 0x40);
        }

        using var app = new TrayApplicationContext(config);
        app.Run();
        return 0;
    }

    // ---------------------------------------------------------------- 공용

    private static string DescribeState(LoudnessEqualizationSetting.Report r) => r.State switch
    {
        LoudnessEqualizationSetting.State.Unsupported => $"[미지원] ({r.Detail})",
        LoudnessEqualizationSetting.State.On => $"[켜짐] ({r.Detail})",
        LoudnessEqualizationSetting.State.Off => $"[꺼짐] ({r.Detail})",
        _ => "?",
    };

    private static int DeviceNotFound(string arg)
    {
        Console.Error.WriteLine($"장치를 찾지 못했습니다: {arg}");
        Console.Error.WriteLine("  `dotnet run -- list`로 장치 번호나 엔드포인트 ID를 확인하세요.");
        return 1;
    }

    private static int Usage()
    {
        Console.WriteLine("사용법:");
        Console.WriteLine("  dotnet run                         트레이 앱 실행 (config.json 대상)");
        Console.WriteLine("  dotnet run -- list                 활성 출력 장치 + Loudness 상태 나열");
        Console.WriteLine("  dotnet run -- set <장치> on|off    트레이와 같은 경로로 Loudness 쓰기");
        Console.WriteLine("  dotnet run -- toggle <장치>        현재 상태의 반대로 토글");
        Console.WriteLine("  dotnet run -- watch <name> [ms]    프로세스 시작/종료 감지 (로그만)");
        Console.WriteLine("  dotnet run -- auto <name> [장치]   프로세스 감지 → Loudness 토글 (장치 없으면 기본 장치)");
        Console.WriteLine("  <장치> = list에 나온 번호(예: 1) 또는 엔드포인트 ID 전체");
        return 2;
    }

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
