using System.Drawing;
using System.Windows.Forms;
using Loudswitch.Audio;
using Loudswitch.Detection;

namespace Loudswitch.Tray;

/// <summary>
/// 트레이 셸: NotifyIcon + 메뉴(활성화 / 상태 / 설정… / 종료).
/// 활성화 시 <see cref="ProcessWatcher"/>로 대상 프로세스를 감시하고 시작=ON / 종료=OFF로 토글한다.
/// 설정 변경은 즉시 재적용한다(재시작 불필요). OFF 복원 여부는 설정을 따른다.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _defaultDeviceTimer = new() { Interval = 3000 };

    private Config _config;
    private string _processName;
    private string? _endpointId;
    private ProcessWatcher? _watcher;
    private bool _enabled;

    public TrayApplicationContext(Config config)
    {
        _config = config;
        _processName = config.ProcessName;
        _endpointId = EndpointResolver.Resolve(config.DeviceGuid);

        _enabledItem = new ToolStripMenuItem("활성화", null, (_, _) => SetEnabled(!_enabled));
        _statusItem = new ToolStripMenuItem("상태") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("설정...", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("종료", null, (_, _) => Quit()));
        menu.Opening += (_, _) => RefreshStatus(); // 메뉴 열 때 현재 상태 갱신(UI 스레드)

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.Create(active: true),
            Visible = true,
            Text = "Loudswitch",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenSettings(); // 더블클릭으로 설정 열기
        _defaultDeviceTimer.Tick += (_, _) => CheckDefaultDevice();

        SetEnabled(true); // 시작 시 활성화(이미 떠 있으면 첫 폴링에서 ON 동기화)
    }

    private void SetEnabled(bool enable)
    {
        _enabled = enable;
        _enabledItem.Checked = enable;

        if (enable)
        {
            StartWatcher();
            _defaultDeviceTimer.Start(); // 자동(기본 장치) 모드일 때 기본 장치 변경 추적
        }
        else
        {
            _defaultDeviceTimer.Stop();
            _watcher?.Dispose();
            _watcher = null;
            if (_config.RestoreOffOnDisable)
                ApplyIfPossible(false); // 비활성화 시 OFF 복원(설정에 따라)
        }

        RefreshStatus();
        UpdateTooltip();
        UpdateIcon();
    }

    private void UpdateIcon()
    {
        Icon? previous = _icon.Icon;
        _icon.Icon = TrayIcons.Create(_enabled); // 활성=컬러 / 비활성=회색
        previous?.Dispose();
    }

    private void StartWatcher()
    {
        _watcher = new ProcessWatcher(_processName, _config.PollingIntervalMs);
        // 콜백은 스레드풀(MTA) 스레드 — UI는 건드리지 않고 토글만 수행(슬라이스 4에서 검증).
        _watcher.Started += _ => ApplyIfPossible(true);
        _watcher.Stopped += () => ApplyIfPossible(false);
        _watcher.Start();
    }

    /// <summary>
    /// "기본 출력 장치(자동)" 모드에서 기본 장치가 바뀌면 대상 엔드포인트를 따라간다.
    /// COM 콜백 대신 폴링(WinForms 타이머, UI 스레드) — 프로세스 감지와 같은 자가치유 방식.
    /// </summary>
    private void CheckDefaultDevice()
    {
        if (!_enabled || _config.DeviceGuid is not null)
            return; // 자동 모드에서만 추적

        string? current = EndpointResolver.Default();
        if (current is null || current == _endpointId)
            return;

        if (_config.RestoreOffOnDisable)
            ApplyIfPossible(false); // 이전 기본 장치 복원

        _endpointId = current;
        _watcher?.Dispose();
        StartWatcher(); // 새 장치에 현재 프로세스 상태 재동기화

        RefreshStatus();
        UpdateTooltip();
    }

    private void ApplyIfPossible(bool on)
    {
        if (_endpointId is not null)
            LoudnessController.Apply(_endpointId, on);
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_config);
        if (form.ShowDialog() != DialogResult.OK)
            return;

        bool wasEnabled = _enabled;
        if (_enabled)
            SetEnabled(false); // 기존 대상에 대해 설정대로 OFF 복원 + 감시 중지

        _config = form.Result;
        _processName = _config.ProcessName;
        _endpointId = EndpointResolver.Resolve(_config.DeviceGuid);

        if (_endpointId is null)
            MessageBox.Show("선택한 출력 장치를 찾지 못했습니다. 감지는 동작하지만 토글은 skip됩니다.",
                "Loudswitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        if (wasEnabled)
        {
            SetEnabled(true); // 새 대상으로 감시 재시작
        }
        else
        {
            RefreshStatus();
            UpdateTooltip();
        }
    }

    private void RefreshStatus()
    {
        string loud;
        if (_endpointId is null)
        {
            loud = "대상 장치 없음";
        }
        else
        {
            loud = LoudnessEqualizationSetting.Read(_endpointId).State switch
            {
                LoudnessEqualizationSetting.State.On => "ON",
                LoudnessEqualizationSetting.State.NotOn => "OFF",
                _ => "미지원",
            };
        }
        _statusItem.Text = $"대상: {_processName} · Loudness {loud} · 감시 {(_enabled ? "ON" : "OFF")}";
    }

    private void UpdateTooltip() =>
        _icon.Text = $"Loudswitch — 감시 {(_enabled ? "ON" : "OFF")}";

    private void Quit()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_config.RestoreOffOnDisable)
            ApplyIfPossible(false); // 종료 시 OFF 복원(설정에 따라)
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _defaultDeviceTimer.Dispose();
            _watcher?.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
