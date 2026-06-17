using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Loudswitch.Audio;
using Loudswitch.Interop;

namespace Loudswitch.Tray;

/// <summary>
/// 설정 창. 사전 지정 프로그램(Valorant/PUBG/Discord)을 토글 버튼으로, "직접 선택"은 현재 실행 중인
/// 프로그램 목록에서 고르거나 직접 입력하도록 제공한다. 레이아웃은 TableLayoutPanel/GroupBox 기반.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int MinIntervalMs = 500;

    // 사전 지정 프로그램 (exe 이름, 표시명).
    private static readonly (string Exe, string Display)[] Presets =
    [
        ("VALORANT-Win64-Shipping", "Valorant"),
        ("TslGame", "PUBG"),
        ("Discord", "Discord"),
    ];

    private readonly List<RadioButton> _presetButtons = [];
    private readonly RadioButton _customButton = new();
    private readonly ComboBox _customCombo = new();
    private readonly ComboBox _device = new();
    private readonly CheckBox _restoreOff = new();
    private readonly CheckBox _autostart = new();
    private readonly NumericUpDown _interval = new();

    public SettingsForm(Config config)
    {
        Result = config;

        Text = "Loudswitch 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(400, 500);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));   // 안내문
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));  // 대상 프로그램
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));   // 출력 장치
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));  // 동작
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // 버튼
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "선택한 프로그램이 켜져 있는 동안 음량 평준화(Loudness EQ)를 자동으로 켜고, 프로그램을 닫으면 다시 끕니다.",
        }, 0, 0);

        root.Controls.Add(BuildTargetGroup(config.ProcessName), 0, 1);
        root.Controls.Add(BuildDeviceGroup(config.DeviceGuid), 0, 2);
        root.Controls.Add(BuildOptionsGroup(config), 0, 3);
        root.Controls.Add(BuildButtons(), 0, 4);
    }

    /// <summary>저장 버튼을 눌렀을 때만 갱신되는 설정(취소 시 원본 그대로).</summary>
    public Config Result { get; private set; }

    private GroupBox BuildTargetGroup(string currentProcess)
    {
        var gb = new GroupBox { Text = "적용할 프로그램", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(8, 4, 8, 8) };

        // 모든 토글 버튼을 같은 TableLayoutPanel에 둬서 자동 단일 선택을 유지한다.
        // 1행: 프리셋 3개 / 2행: '직접 선택' 버튼 + 콤보를 나란히(연관성 표시).
        var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 안내
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // 프리셋 한 줄
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 직접 선택 + 콤보 한 줄

        Label helper = Hint("실행할 프로그램을 고르세요. '직접 선택'은 지금 실행 중인 프로그램에서 지정합니다.");
        tlp.Controls.Add(helper, 0, 0);
        tlp.SetColumnSpan(helper, 3);

        int col = 0;
        foreach ((string exe, string display) in Presets)
        {
            var rb = MakeToggle(display, exe, AppIcons.For(exe, display));
            _presetButtons.Add(rb);
            tlp.Controls.Add(rb, col++, 1);
        }

        _customButton.Appearance = Appearance.Button;
        _customButton.Text = "직접 선택";
        _customButton.TextAlign = ContentAlignment.MiddleCenter;
        _customButton.Dock = DockStyle.Fill;
        _customButton.Margin = new Padding(3);
        _customButton.CheckedChanged += (_, _) =>
        {
            _customCombo.Enabled = _customButton.Checked;
            if (_customButton.Checked)
                _customCombo.Focus();
        };
        tlp.Controls.Add(_customButton, 0, 2);

        _customCombo.Dock = DockStyle.Fill;
        _customCombo.Margin = new Padding(3);
        _customCombo.DropDownStyle = ComboBoxStyle.DropDown; // 직접 입력 가능
        _customCombo.Enabled = false;
        PopulateRunningProcesses();
        tlp.Controls.Add(_customCombo, 1, 2);
        tlp.SetColumnSpan(_customCombo, 2);

        gb.Controls.Add(tlp);
        SelectProcess(currentProcess);
        return gb;
    }

    private RadioButton MakeToggle(string text, string exe, Image icon) => new()
    {
        Appearance = Appearance.Button,
        Text = text,
        Tag = exe,
        Image = icon,
        ImageAlign = ContentAlignment.MiddleLeft,
        TextImageRelation = TextImageRelation.ImageBeforeText,
        TextAlign = ContentAlignment.MiddleCenter,
        Dock = DockStyle.Fill,
        Margin = new Padding(3),
    };

    private void PopulateRunningProcesses()
    {
        Process[] all = [];
        try
        {
            all = Process.GetProcesses();
            string[] names = all
                .Select(p => p.ProcessName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _customCombo.Items.AddRange(names);
        }
        catch
        {
            // 열거 실패해도 직접 입력은 가능
        }
        finally
        {
            foreach (Process p in all)
                p.Dispose();
        }
    }

    private GroupBox BuildDeviceGroup(string? currentGuid)
    {
        var gb = new GroupBox { Text = "출력 장치", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(8, 6, 8, 8) };

        var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        _device.Dock = DockStyle.Fill;
        _device.DropDownStyle = ComboBoxStyle.DropDownList;
        PopulateDevices(currentGuid);
        tlp.Controls.Add(_device, 0, 0);
        tlp.Controls.Add(Hint("음량 평준화를 지원하는 장치에만 적용됩니다(미지원 장치는 건너뜀)."), 0, 1);

        gb.Controls.Add(tlp);
        return gb;
    }

    private GroupBox BuildOptionsGroup(Config config)
    {
        var gb = new GroupBox { Text = "동작", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(8, 6, 8, 8) };

        var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4 };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        _autostart.Text = "Windows 시작 시 자동 실행";
        _autostart.AutoSize = true;
        _autostart.Anchor = AnchorStyles.Left;
        _autostart.Checked = Autostart.IsEnabled();
        tlp.Controls.Add(_autostart, 0, 0);
        tlp.SetColumnSpan(_autostart, 3);

        _restoreOff.Text = "감시를 끄거나 앱을 종료하면 평준화도 함께 끄기";
        _restoreOff.AutoSize = true;
        _restoreOff.Anchor = AnchorStyles.Left;
        _restoreOff.Checked = config.RestoreOffOnDisable;
        tlp.Controls.Add(_restoreOff, 0, 1);
        tlp.SetColumnSpan(_restoreOff, 3);

        tlp.Controls.Add(new Label { Text = "실행 확인 주기", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 8, 0) }, 0, 2);
        _interval.Anchor = AnchorStyles.Left;
        _interval.Width = 80;
        _interval.Minimum = MinIntervalMs;
        _interval.Maximum = 60000;
        _interval.Increment = 100;
        _interval.Value = Math.Clamp(config.PollingIntervalMs, MinIntervalMs, 60000);
        tlp.Controls.Add(_interval, 1, 2);
        tlp.Controls.Add(new Label { Text = "ms", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 5, 0, 0) }, 2, 2);

        Label hint = Hint("작을수록 빨리 반응합니다. 최소 500ms, 보통 1500ms.");
        tlp.Controls.Add(hint, 0, 3);
        tlp.SetColumnSpan(hint, 3);

        gb.Controls.Add(tlp);
        return gb;
    }

    private FlowLayoutPanel BuildButtons()
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 2, 0, 0) };

        var save = new Button { Text = "저장(&S)", DialogResult = DialogResult.OK, Size = new Size(84, 28) };
        save.Click += OnSave;
        var cancel = new Button { Text = "취소(&C)", DialogResult = DialogResult.Cancel, Size = new Size(84, 28) };

        flow.Controls.Add(cancel); // RightToLeft → 가장 오른쪽
        flow.Controls.Add(save);   // 그 왼쪽

        AcceptButton = save;
        CancelButton = cancel;
        return flow;
    }

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill, // 셀 폭에 맞춰 줄바꿈 → 오른쪽 잘림 방지
        ForeColor = SystemColors.GrayText, // OS 힌트 색(테마 아님)
    };

    private void SelectProcess(string processName)
    {
        string target = Strip(processName);
        RadioButton? match = _presetButtons.FirstOrDefault(
            r => string.Equals(Strip((string)r.Tag!), target, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            match.Checked = true;
        }
        else
        {
            _customButton.Checked = true; // 콤보 활성화는 CheckedChanged에서
            _customCombo.Text = processName;
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        string proc;
        if (_customButton.Checked)
        {
            proc = _customCombo.Text.Trim();
            if (proc.Length == 0)
            {
                Warn("직접 선택할 프로그램을 고르거나 이름을 입력하세요.");
                return;
            }
        }
        else
        {
            RadioButton? sel = _presetButtons.FirstOrDefault(r => r.Checked);
            if (sel is null)
            {
                Warn("적용할 프로그램을 선택하세요.");
                return;
            }
            proc = (string)sel.Tag!;
        }

        var cfg = new Config
        {
            ProcessName = proc,
            DeviceGuid = (_device.SelectedItem as DeviceItem)?.DeviceGuid, // null = 기본 장치
            PollingIntervalMs = (int)_interval.Value,
            RestoreOffOnDisable = _restoreOff.Checked,
        };
        cfg.Save();
        Autostart.SetEnabled(_autostart.Checked); // 레지스트리 Run 키(별도 영속)
        Result = cfg;
    }

    private void Warn(string message)
    {
        MessageBox.Show(message, "Loudswitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        DialogResult = DialogResult.None; // 창 닫지 않음
    }

    private void PopulateDevices(string? currentGuid)
    {
        _device.Items.Add(new DeviceItem("기본 출력 장치 (자동 선택)", null));

        try
        {
            foreach (string id in CoreAudio.GetRenderEndpointIds(activeOnly: true))
            {
                string guid = MMDevicePaths.DeviceGuid(id);
                string shortId = guid.Trim('{', '}');
                if (shortId.Length > 8)
                    shortId = shortId[..8];
                bool supported = LoudnessEqualizationSetting.Read(id).State
                    != LoudnessEqualizationSetting.State.Unsupported;
                _device.Items.Add(new DeviceItem(
                    $"{EndpointName.Read(id)} · {(supported ? "지원" : "미지원")} · {shortId}", guid));
            }
        }
        catch
        {
            // 열거 실패 시 기본 장치 옵션만 노출
        }

        _device.SelectedIndex = FindDeviceIndex(currentGuid);
    }

    private int FindDeviceIndex(string? currentGuid)
    {
        if (currentGuid is null)
            return 0;

        string norm = MMDevicePaths.NormalizeGuid(currentGuid);
        for (int i = 0; i < _device.Items.Count; i++)
        {
            if (_device.Items[i] is DeviceItem di && di.DeviceGuid is not null &&
                string.Equals(MMDevicePaths.NormalizeGuid(di.DeviceGuid), norm, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    private static string Strip(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private sealed class DeviceItem(string display, string? deviceGuid)
    {
        public string? DeviceGuid { get; } = deviceGuid;
        private string Display { get; } = display;
        public override string ToString() => Display;
    }
}
