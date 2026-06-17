using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
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

        string? endpointId = EndpointResolver.Resolve(args[1]);
        if (endpointId is null)
            return DeviceNotFound(args[1]);

        return ApplyAndReport(endpointId, on: mode == "on");
    }

    private static int RunToggle(string[] args)
    {
        // toggle <guid|endpointId>
        if (args.Length < 2)
            return Usage();

        string? endpointId = EndpointResolver.Resolve(args[1]);
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
        int interval = Config.DefaultPollingIntervalMs;
        if (args.Length >= 3 && int.TryParse(args[2], out int n) && n >= Config.MinPollingIntervalMs)
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

        string? endpointId = EndpointResolver.Resolve(args.Length >= 3 ? args[2] : null);
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

    private static void ApplyAuto(string endpointId, bool on)
    {
        switch (LoudnessController.Apply(endpointId, on))
        {
            case LoudnessController.ApplyResult.SkippedUnsupported:
                Log("  → 미지원 (키 없음) → 토글 skip (force-create 안 함)");
                break;
            case LoudnessController.ApplyResult.Failed:
                Log("  → SetPropertyValue 실패 (S_OK 아님)");
                break;
            case LoudnessController.ApplyResult.Set:
                Log($"  → OK, 상태: {DescribeState(LoudnessEqualizationSetting.Read(endpointId))}");
                break;
        }
    }

    // ---------------------------------------------------------------- 슬라이스 5: 트레이

    private static int RunTray()
    {
        // DPI/렌더링 설정은 어떤 창(MessageBox 포함)보다 먼저 적용해야 한다.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 단일 인스턴스: 이미 실행 중이면 알리고 종료. (Mutex는 프로세스 생명주기 동안 보관)
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\Loudswitch.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Loudswitch가 이미 실행 중입니다. 시스템 트레이를 확인하세요.",
                "Loudswitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Config config = Config.LoadOrCreateDefault(out bool created);

        if (created)
        {
            MessageBox.Show(
                $"설정 파일을 생성했습니다:\n{Config.FilePath}\n\n트레이 아이콘 우클릭 → '설정...'에서 적용할 프로그램·장치를 지정하세요.",
                "Loudswitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        Application.Run(new TrayApplicationContext(config));
        return 0;
    }

    // ---------------------------------------------------------------- 공용

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
        Console.WriteLine("  dotnet run                         트레이 앱 실행 (config.json 대상)");
        Console.WriteLine("  dotnet run -- list                 활성 출력 장치 + Loudness 상태 나열");
        Console.WriteLine("  dotnet run -- set <guid> on|off    IPolicyConfig로 Loudness 쓰기");
        Console.WriteLine("  dotnet run -- toggle <guid>        현재 상태의 반대로 토글");
        Console.WriteLine("  dotnet run -- watch <name> [ms]    프로세스 시작/종료 감지 (로그만)");
        Console.WriteLine("  dotnet run -- auto <name> [guid]   프로세스 감지 → Loudness 토글 (guid 없으면 기본 장치)");
        return 2;
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
