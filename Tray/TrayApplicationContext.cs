using System.Diagnostics;
using Loudswitch.Audio;
using Loudswitch.Detection;
using Loudswitch.Interop;

namespace Loudswitch.Tray;

/// <summary>
/// 트레이 컨트롤러(WinForms 없음). 앱이 실행 중인 동안 항상 대상 프로세스를 감시해
/// 시작=ON / 종료=OFF로 Loudness EQ를 토글한다(멈추려면 앱 종료). 별도 "활성화" 토글은 없다.
/// 트레이 아이콘은 현재 Loudness 상태(켜짐=컬러 / 꺼짐=회색)를 표시한다.
/// </summary>
internal sealed class TrayApplicationContext : IDisposable
{
    // 사전 지정 프로그램 (exe 이름, 표시명).
    private static readonly (string Exe, string Display)[] Presets =
    [
        ("VALORANT-Win64-Shipping", "Valorant"),
        ("TslGame", "PUBG"),
        ("Discord", "Discord"),
    ];

    private const uint DefaultDeviceTimerMs = 3000;

    private readonly TrayShell _shell;

    private Config _config;
    private string _processName;
    private string? _endpointId;
    private ProcessWatcher? _watcher;

    public TrayApplicationContext(Config config)
    {
        _config = config;
        _processName = config.ProcessName;
        _endpointId = EndpointResolver.Resolve(config.DeviceGuid);

        _shell = new TrayShell("Loudswitch");
        _shell.MenuRequested = ShowMenu;
        _shell.TimerTick = CheckDefaultDevice;

        StartWatching(); // 실행 = 동작. 멈추려면 종료.
        UpdateIcon();
    }

    /// <summary>메시지 루프 실행(블로킹). 종료 시 정리.</summary>
    public void Run()
    {
        _shell.Run();
        Dispose();
    }

    // ---------------------------------------------------------------- 메뉴

    private void ShowMenu()
    {
        _shell.ShowMenu(m =>
        {
            m.Label($"Loudswitch — {StatusText()}");
            m.Separator();
            m.Submenu("적용할 프로그램", BuildProgramMenu);
            m.Submenu("출력 장치", BuildDeviceMenu);
            m.Separator();
            m.Check("토글 시 알림 표시", _config.NotifyOnToggle, ToggleNotify);
            m.Check("Windows 시작 시 자동 실행", Autostart.IsEnabled(), ToggleAutostart);
            m.Check("종료 시 평준화도 끄기", _config.RestoreOffOnDisable, ToggleRestoreOff);
            m.Separator();
            m.Item("설정 파일 열기…", OpenConfigFile);
            m.Item("종료", Quit);
        });
    }

    private void BuildProgramMenu(SubMenu sub)
    {
        foreach ((string exe, string display) in Presets)
            sub.Check(display, IsSelectedProcess(exe), () => SelectProcess(exe));

        sub.Separator();
        sub.Submenu("실행 중에서 선택", BuildRunningMenu);
        sub.Item("프로그램 찾아보기…", BrowseForProgram);
    }

    private void BuildRunningMenu(SubMenu sub)
    {
        IReadOnlyList<string> names = RunningProcessNames();
        if (names.Count == 0)
        {
            sub.Label("(창 있는 프로그램 없음)");
            return;
        }
        foreach (string n in names)
            sub.Check(n, IsSelectedProcess(n), () => SelectProcess(n));
    }

    private void BuildDeviceMenu(SubMenu sub)
    {
        sub.Check("기본 출력 장치 (자동 선택)", _config.DeviceGuid is null, () => SelectDevice(null));

        IReadOnlyList<string> ids;
        try
        {
            ids = CoreAudio.GetRenderEndpointIds(activeOnly: true);
        }
        catch
        {
            return; // 열거 실패 시 기본 장치 옵션만
        }

        foreach (string id in ids)
        {
            string guid = MMDevicePaths.DeviceGuid(id);
            bool supported = LoudnessEqualizationSetting.Read(id).State
                != LoudnessEqualizationSetting.State.Unsupported;
            bool selected = _config.DeviceGuid is not null &&
                string.Equals(MMDevicePaths.NormalizeGuid(guid), MMDevicePaths.NormalizeGuid(_config.DeviceGuid),
                    StringComparison.OrdinalIgnoreCase);

            sub.Check($"{EndpointName.Read(id)} · {(supported ? "지원" : "미지원")}", selected, () => SelectDevice(guid));
        }
    }

    // ---------------------------------------------------------------- 메뉴 액션

    private void SelectProcess(string name)
    {
        _config.ProcessName = name;
        _config.Save();
        Reconfigure();
    }

    private void SelectDevice(string? guid)
    {
        _config.DeviceGuid = guid;
        _config.Save();
        Reconfigure();
    }

    private void ToggleNotify()
    {
        _config.NotifyOnToggle = !_config.NotifyOnToggle;
        _config.Save();
    }

    private void ToggleAutostart() => Autostart.SetEnabled(!Autostart.IsEnabled());

    private void ToggleRestoreOff()
    {
        _config.RestoreOffOnDisable = !_config.RestoreOffOnDisable;
        _config.Save();
    }

    private void OpenConfigFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Config.FilePath) { UseShellExecute = true });
        }
        catch
        {
            _shell.Warn($"설정 파일을 열지 못했습니다:\n{Config.FilePath}");
        }
    }

    private void BrowseForProgram()
    {
        string? path = _shell.BrowseForExecutable("적용할 프로그램(.exe) 선택");
        if (path is null)
            return;

        string name = Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrWhiteSpace(name))
            SelectProcess(name);
    }

    // ---------------------------------------------------------------- 코어 로직

    /// <summary>설정 변경 후 대상 재해석 + 감시 재시작(이전 대상엔 설정대로 OFF 복원).</summary>
    private void Reconfigure()
    {
        StopWatching(restore: true); // 이전 _endpointId 기준 OFF 복원 + 감시/타이머 중지
        _processName = _config.ProcessName;
        _endpointId = EndpointResolver.Resolve(_config.DeviceGuid);

        if (_endpointId is null && !string.IsNullOrWhiteSpace(_config.ProcessName))
            _shell.Warn("선택한 출력 장치를 찾지 못했습니다. 감지는 동작하지만 토글은 skip됩니다.");

        StartWatching();
        UpdateIcon();
    }

    private void StartWatching()
    {
        if (_config.DeviceGuid is null)
            _shell.StartDefaultDeviceTimer(DefaultDeviceTimerMs); // 자동 모드에서만 기본 장치 추적
        StartWatcher();
    }

    private void StopWatching(bool restore)
    {
        _shell.StopDefaultDeviceTimer();
        _watcher?.Dispose();
        _watcher = null;
        if (restore && _config.RestoreOffOnDisable)
            Apply(false, notify: false);
    }

    private void StartWatcher()
    {
        if (string.IsNullOrWhiteSpace(_processName))
            return; // 대상 프로그램 미설정 → 감시/토글 안 함

        // syncInitialState=true: 시작 시 대상이 떠 있지 않으면 OFF로 동기화(크래시 잔존 ON 정리).
        _watcher = new ProcessWatcher(_processName, syncInitialState: true);
        // 콜백은 스레드풀 스레드 — COM/Shell_NotifyIcon은 스레드 무관하게 안전.
        _watcher.Started += _ => Apply(true, notify: true);
        _watcher.Stopped += () => Apply(false, notify: true);
        _watcher.Start();
    }

    /// <summary>
    /// "기본 출력 장치(자동)" 모드에서 기본 장치가 바뀌면 대상 엔드포인트를 따라간다.
    /// Win32 타이머(메시지 루프 스레드) 폴링 — 프로세스 감지와 같은 자가치유 방식.
    /// </summary>
    private void CheckDefaultDevice()
    {
        if (_config.DeviceGuid is not null)
            return; // 자동 모드에서만 추적

        string? current = EndpointResolver.Default();
        if (current is null || current == _endpointId)
            return;

        if (_config.RestoreOffOnDisable)
            Apply(false, notify: false); // 이전 기본 장치 복원

        _endpointId = current;
        _watcher?.Dispose();
        StartWatcher(); // 새 장치에 현재 프로세스 상태 재동기화 (타이머는 계속)
        UpdateIcon();
    }

    /// <summary>
    /// Loudness를 토글하고 아이콘을 갱신한다. <paramref name="notify"/>가 true이고 <b>실제 상태가
    /// 바뀐 경우에만</b> 알림을 띄운다(설정에 따라). 내부 housekeeping은 notify=false로 조용히 처리.
    /// </summary>
    private void Apply(bool on, bool notify)
    {
        if (_endpointId is null)
            return;

        bool wasOn = LoudnessEqualizationSetting.Read(_endpointId).State == LoudnessEqualizationSetting.State.On;
        if (LoudnessController.Apply(_endpointId, on) != LoudnessController.ApplyResult.Set)
            return;

        UpdateIcon();
        if (notify && wasOn != on && _config.NotifyOnToggle)
            _shell.Notify("Loudswitch", on ? "음량 평준화를 켰습니다." : "음량 평준화를 껐습니다.");
    }

    private void Quit()
    {
        StopWatching(restore: true); // 종료 시 OFF 복원(설정에 따라) + 감시 중지
        _shell.Quit();               // 메시지 루프 종료 → Run() 반환 → Dispose
    }

    // ---------------------------------------------------------------- 표시/공용

    /// <summary>현재 Loudness 상태를 아이콘(켜짐=컬러 / 꺼짐=회색)과 툴팁에 반영.</summary>
    private void UpdateIcon()
    {
        bool on = _endpointId is not null &&
            LoudnessEqualizationSetting.Read(_endpointId).State == LoudnessEqualizationSetting.State.On;
        _shell.SetActive(on, $"Loudswitch — 평준화 {(on ? "켜짐" : "꺼짐")}");
    }

    private string StatusText()
    {
        string loud = _endpointId is null
            ? "대상 장치 없음"
            : LoudnessEqualizationSetting.Read(_endpointId).State switch
            {
                LoudnessEqualizationSetting.State.On => "평준화 켜짐",
                LoudnessEqualizationSetting.State.NotOn => "평준화 꺼짐",
                _ => "미지원 장치",
            };
        string target = string.IsNullOrWhiteSpace(_processName) ? "(미설정)" : _processName;
        return $"대상: {target} · {loud}";
    }

    private bool IsSelectedProcess(string exe) =>
        !string.IsNullOrWhiteSpace(_processName) &&
        string.Equals(Strip(exe), Strip(_processName), StringComparison.OrdinalIgnoreCase);

    private static string Strip(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static IReadOnlyList<string> RunningProcessNames()
    {
        Process[] all = [];
        try
        {
            all = Process.GetProcesses();
            return all
                .Where(HasVisibleWindow) // 창 있는 사용자 앱만 → 시스템 프로세스 노이즈 제거
                .Select(p => p.ProcessName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
        finally
        {
            foreach (Process p in all)
                p.Dispose();
        }
    }

    private static bool HasVisibleWindow(Process p)
    {
        try
        {
            return p.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _shell.Dispose();
    }
}
