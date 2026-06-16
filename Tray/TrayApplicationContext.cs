using System.Drawing;
using System.Windows.Forms;
using Loudswitch.Audio;
using Loudswitch.Detection;

namespace Loudswitch.Tray;

/// <summary>
/// 트레이 셸: NotifyIcon + 메뉴(활성화 토글 / 상태 / 종료).
/// 활성화 시 <see cref="ProcessWatcher"/>로 대상 프로세스를 감시하고 시작=ON / 종료=OFF로 토글한다.
/// 비활성화·앱 종료 시 Loudness를 OFF로 복원한다(미지원 장치는 건드리지 않음).
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly string _processName;
    private readonly string _endpointId;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _statusItem;

    private ProcessWatcher? _watcher;
    private bool _enabled;

    public TrayApplicationContext(string processName, string endpointId)
    {
        _processName = processName;
        _endpointId = endpointId;

        _enabledItem = new ToolStripMenuItem("활성화", null, (_, _) => SetEnabled(!_enabled));
        _statusItem = new ToolStripMenuItem("상태") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("종료", null, (_, _) => Quit()));
        menu.Opening += (_, _) => RefreshStatus(); // 메뉴 열 때 현재 상태 갱신(UI 스레드)

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Loudswitch",
            ContextMenuStrip = menu,
        };

        SetEnabled(true); // 시작 시 활성화(이미 떠 있으면 첫 폴링에서 ON 동기화)
    }

    private void SetEnabled(bool enable)
    {
        _enabled = enable;
        _enabledItem.Checked = enable;

        if (enable)
        {
            _watcher = new ProcessWatcher(_processName);
            // 콜백은 스레드풀(MTA) 스레드 — UI는 건드리지 않고 토글만 수행(슬라이스 4에서 검증).
            _watcher.Started += _ => LoudnessController.Apply(_endpointId, true);
            _watcher.Stopped += () => LoudnessController.Apply(_endpointId, false);
            _watcher.Start();
        }
        else
        {
            _watcher?.Dispose();
            _watcher = null;
            LoudnessController.Apply(_endpointId, false); // 비활성화 시 OFF 복원
        }

        RefreshStatus();
        UpdateTooltip();
    }

    private void RefreshStatus()
    {
        LoudnessEqualizationSetting.Report st = LoudnessEqualizationSetting.Read(_endpointId);
        string loud = st.State switch
        {
            LoudnessEqualizationSetting.State.On => "ON",
            LoudnessEqualizationSetting.State.NotOn => "OFF",
            _ => "미지원",
        };
        _statusItem.Text = $"대상: {_processName} · Loudness {loud} · 감시 {(_enabled ? "ON" : "OFF")}";
    }

    private void UpdateTooltip() =>
        _icon.Text = $"Loudswitch — 감시 {(_enabled ? "ON" : "OFF")}";

    private void Quit()
    {
        _watcher?.Dispose();
        _watcher = null;
        LoudnessController.Apply(_endpointId, false); // 종료 시 OFF 복원
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher?.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
