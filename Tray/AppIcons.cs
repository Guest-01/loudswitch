using System.Drawing;
using System.IO;
using Microsoft.Win32;

namespace Loudswitch.Tray;

/// <summary>
/// 사전 지정 프로세스용 16x16 아이콘을 얻는다.
/// App Paths 레지스트리에서 실제 실행 파일을 찾아 아이콘을 추출하고, 실패하면 글자 배지로 폴백.
/// (테마가 아니라 라디오 항목에 붙일 콘텐츠 이미지)
/// </summary>
internal static class AppIcons
{
    public static Image For(string exeName, string display)
    {
        try
        {
            string? path = FindExePath(exeName);
            if (path is not null)
            {
                using Icon ico = Icon.ExtractAssociatedIcon(path)!;
                return new Bitmap(ico.ToBitmap(), new Size(16, 16));
            }
        }
        catch
        {
            // 경로/아이콘 추출 실패 → 글자 배지로 폴백
        }
        return LetterBadge(display);
    }

    private static string? FindExePath(string exeName)
    {
        string exe = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe";
        foreach (RegistryKey root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using RegistryKey? key = root.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string p)
            {
                p = p.Trim('"');
                if (File.Exists(p))
                    return p;
            }
        }
        return null;
    }

    private static Image LetterBadge(string display)
    {
        var bmp = new Bitmap(16, 16);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var bg = new SolidBrush(Color.FromArgb(120, 120, 120));
        g.FillRectangle(bg, 0, 0, 16, 16);

        string letter = string.IsNullOrEmpty(display) ? "?" : display[..1].ToUpperInvariant();
        using var fg = new SolidBrush(Color.White);
        using var font = new Font("Segoe UI", 8F, FontStyle.Bold);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(letter, font, fg, new RectangleF(0, 0, 16, 16), fmt);
        return bmp;
    }
}
