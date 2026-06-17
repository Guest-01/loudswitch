using System.Windows.Forms;
using Microsoft.Win32;

namespace Loudswitch.Tray;

/// <summary>
/// 로그인 시 자동 실행(HKCU Run 키). 관리자 권한 불필요.
/// </summary>
internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Loudswitch";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enable)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enable)
            key.SetValue(ValueName, $"\"{ExecutablePath()}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string ExecutablePath() =>
        Environment.ProcessPath ?? Application.ExecutablePath;
}
