using System.Diagnostics;
using Loudswitch.Audio;
using Loudswitch.Detection;
using Loudswitch.Interop;

namespace Loudswitch.Tray;

/// <summary>
/// 트레이 컨트롤러(WinForms 없음). 앱이 실행 중인 동안 항상 대상 프로세스를 감시해
/// 시작=ON / 종료=<b>원래 상태로 복원</b>으로 Loudness EQ를 다룬다(멈추려면 앱 종료). 별도 "활성화" 토글은 없다.
/// 트레이 아이콘은 현재 Loudness 상태(켜짐=컬러 / 꺼짐=회색)를 표시한다.
///
/// 복원 규칙: 앱이 평준화를 <b>직접 켠 장치만</b> <see cref="RestoreState"/>에 "원래 꺼짐"으로 기록하고,
/// 대상 프로그램이 끝나면 그 기록대로 되돌린다. 사용자가 이미 켜 두었던 장치는 기록하지 않아 그대로 둔다.
/// 기록은 파일로 남으므로 앱이 비정상 종료돼도 다음 실행 때 대상이 없으면 되돌린다.
///
/// 스레드 모델: 모든 상태(_config, _endpointId, _watcher 등)는 <b>메시지 루프 스레드에서만</b> 읽고 쓴다.
/// 메뉴·타이머는 원래 그 스레드에서 오고, 감시 콜백(스레드풀)은 <see cref="TrayShell.Post"/>로 넘겨온다.
/// 그래서 잠금이 필요 없다.
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
    private readonly RestoreState _restore = RestoreState.Load();

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
            m.Check("앱 종료·설정 변경 시에도 원래대로 되돌리기", _config.RestoreOnExit, ToggleRestoreOnExit);
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

    private void ToggleRestoreOnExit()
    {
        _config.RestoreOnExit = !_config.RestoreOnExit;
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

    /// <summary>설정 변경 후 대상 재해석 + 감시 재시작(앱이 바꿔 둔 장치는 설정대로 원래 상태로).</summary>
    private void Reconfigure()
    {
        StopWatching(restore: true); // 감시/타이머 중지 + 원래 상태 복원(설정에 따라)
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
        if (restore)
            RestoreOrForget();
    }

    /// <summary>
    /// 앱 종료·설정 변경·기본 장치 변경 때 호출. 설정이 켜져 있으면 원래 상태로 되돌리고,
    /// 꺼져 있으면 기록만 지운다(평준화는 지금 상태 그대로 둠).
    /// </summary>
    private void RestoreOrForget()
    {
        if (_config.RestoreOnExit)
        {
            RestoreAll(notify: false);
        }
        else if (_restore.Pending.Count > 0)
        {
            _restore.Pending.Clear();
            _restore.Save();
        }
    }

    private void StartWatcher()
    {
        if (string.IsNullOrWhiteSpace(_processName))
            return; // 대상 프로그램 미설정 → 감시/토글 안 함

        // syncInitialState=true: 시작 시 대상이 떠 있지 않으면 Stopped가 한 번 발생 →
        // 이전 실행(비정상 종료 포함)이 남긴 복원 기록을 처리한다. 기록이 없으면 아무것도 바꾸지 않는다.
        var watcher = new ProcessWatcher(_processName, syncInitialState: true);
        _watcher = watcher;

        // 콜백은 스레드풀 스레드에서 온다 → 직접 상태를 만지지 않고 메시지 루프 스레드로 넘긴다.
        // 거기서 "아직 현재 감시자인가"를 확인해, 장치/대상 변경으로 교체된 옛 감시자의
        // 늦게 도착한 이벤트가 새 대상(엔드포인트)에 잘못 적용되는 것을 막는다.
        watcher.Started += _ => _shell.Post(() =>
        {
            if (_watcher == watcher)
                OnTargetStarted();
        });
        watcher.Stopped += () => _shell.Post(() =>
        {
            if (_watcher == watcher)
                RestoreAll(notify: true);
        });
        watcher.Start();
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

        RestoreOrForget(); // 이전 기본 장치를 원래 상태로(설정에 따라)

        _endpointId = current;
        _watcher?.Dispose();
        _watcher = null; // StartWatcher가 일찍 반환해도 옛 감시자가 "현재"로 남지 않도록
        StartWatcher(); // 새 장치에 현재 프로세스 상태 재동기화 (타이머는 계속)
        UpdateIcon();
    }

    /// <summary>
    /// 대상 프로그램 시작. 평준화가 꺼져 있을 때만 켜고, 그 장치를 "원래 꺼짐"으로 기록한다.
    /// 이미 켜져 있으면(사용자가 켜 둠) 건드리지도 기록하지도 않는다 → 종료 후에도 켜진 채 유지.
    /// </summary>
    private void OnTargetStarted()
    {
        if (_endpointId is null)
            return;
        string endpointId = _endpointId;

        LoudnessEqualizationSetting.Report before = LoudnessEqualizationSetting.Read(endpointId);
        if (before.State != LoudnessEqualizationSetting.State.Off)
        {
            UpdateIcon(); // 미지원이거나 이미 켜짐 → 바꿀 것 없음
            return;
        }

        // 기록을 먼저 남기고 켠다: 켠 직후 비정상 종료돼도 다음 실행 때 되돌릴 수 있도록.
        bool newlyRecorded = _restore.Pending.TryAdd(endpointId, false);
        if (newlyRecorded)
            _restore.Save();

        LoudnessController.ApplyResult result = LoudnessController.Apply(endpointId, true);
        if (result != LoudnessController.ApplyResult.Set && newlyRecorded)
        {
            _restore.Pending.Remove(endpointId); // 켜지 못했으면 되돌릴 것도 없다
            _restore.Save();
        }

        UpdateIcon();
        if (result == LoudnessController.ApplyResult.Set && _config.NotifyOnToggle)
            _shell.Notify("Loudswitch", "음량 평준화를 켰습니다.");
    }

    /// <summary>
    /// 기록된 장치를 모두 원래 상태로 되돌린다(대상 프로그램 종료 시, 또는 설정에 따라 앱 종료·변경 시).
    /// 현재 대상 장치가 아니어도 기록된 장치 그대로 되돌린다(장치 변경·비정상 종료 대비).
    /// 쓰기에 실패한 장치는 기록을 남겨 다음 기회에 다시 시도한다.
    /// </summary>
    private void RestoreAll(bool notify)
    {
        if (_restore.Pending.Count == 0)
            return;

        bool changed = false;
        foreach ((string endpointId, bool originalOn) in _restore.Pending.ToArray())
        {
            LoudnessController.ApplyResult result = LoudnessController.Apply(endpointId, originalOn);
            if (result == LoudnessController.ApplyResult.Failed)
                continue;

            _restore.Pending.Remove(endpointId); // Set / Unchanged / 미지원(장치 사라짐 등) → 기록 정리
            changed |= result == LoudnessController.ApplyResult.Set;
        }
        _restore.Save();

        UpdateIcon();
        if (notify && changed && _config.NotifyOnToggle)
            _shell.Notify("Loudswitch", "음량 평준화를 원래 상태로 되돌렸습니다.");
    }

    private void Quit()
    {
        StopWatching(restore: true); // 감시 중지 + 원래 상태 복원(설정에 따라)
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
                LoudnessEqualizationSetting.State.Off => "평준화 꺼짐",
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
