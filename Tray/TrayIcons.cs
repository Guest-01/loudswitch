using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Loudswitch.Tray;

/// <summary>
/// 트레이 아이콘을 코드로 생성한다(에셋 없이). 둥근 사각 배지 + 흰 "L" — 활성=파랑, 비활성=회색.
/// </summary>
internal static class TrayIcons
{
    public static Icon Create(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            Color bg = active ? Color.FromArgb(0, 120, 215) : Color.FromArgb(150, 150, 150);
            var rect = new Rectangle(1, 1, 30, 30);

            using (GraphicsPath path = RoundedRect(rect, 6))
            using (var brush = new SolidBrush(bg))
                g.FillPath(brush, path);

            using var font = new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("L", font, Brushes.White, rect, fmt);
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone(); // HICON과 독립된 복제본 → 원본 핸들 해제 가능
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
