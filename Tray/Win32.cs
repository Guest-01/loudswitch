using System.Runtime.InteropServices;

namespace Loudswitch.Tray;

/// <summary>
/// 트레이 셸/메뉴/대화상자에 필요한 Win32 P/Invoke. 전부 LibraryImport(소스 생성)라 NativeAOT 친화적.
/// </summary>
internal static unsafe partial class Win32
{
    // 윈도우 메시지
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_APP_TRAY = 0x0400 + 1; // 트레이 콜백(WM_USER 영역)
    public const uint WM_APP_INVOKE = 0x0400 + 2; // 다른 스레드가 Post한 작업 실행 요청
    public const uint WM_CONTEXTMENU = 0x007B;

    // 트레이 콜백 이벤트 (NOTIFYICON_VERSION_4: lParam 하위 워드)
    public const uint NIN_SELECT = 0x0400;    // 마우스 왼쪽 클릭
    public const uint NIN_KEYSELECT = 0x0401; // 키보드로 선택(Enter/Space)

    // 메뉴 플래그
    public const uint MF_STRING = 0x0000;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_POPUP = 0x0010;

    // TrackPopupMenu
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_RIGHTALIGN = 0x0008;
    public const int SM_MENUDROPALIGNMENT = 40;

    // Shell_NotifyIcon
    public const uint NIM_ADD = 0x0;
    public const uint NIM_MODIFY = 0x1;
    public const uint NIM_DELETE = 0x2;
    public const uint NIM_SETVERSION = 0x4;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIF_MESSAGE = 0x1;
    public const uint NIF_ICON = 0x2;
    public const uint NIF_TIP = 0x4;
    public const uint NIF_INFO = 0x10;       // 풍선/토스트 알림(szInfo/szInfoTitle)
    public const uint NIF_SHOWTIP = 0x80;    // 버전 4에서도 표준 툴팁 표시(없으면 툴팁이 숨겨짐)
    public const uint NIIF_USER = 0x4;       // 알림 아이콘으로 hBalloonIcon(없으면 hIcon)을 사용
    public const uint NIIF_LARGE_ICON = 0x20; // 큰 아이콘 사용

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll")]
    public static partial ushort RegisterClassExW(in WNDCLASSEXW wc);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint hInstance, nint param);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(out MSG msg, nint hWnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    public static partial int TranslateMessage(in MSG msg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(in MSG msg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll")]
    public static partial int PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    public static partial int AppendMenuW(nint hMenu, uint flags, nint idNewItem, nint newItem);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenu(nint hMenu, uint flags, int x, int y, int reserved, nint hWnd, nint rect);

    [LibraryImport("user32.dll")]
    public static partial int DestroyMenu(nint hMenu);

    [LibraryImport("user32.dll")]
    public static partial int GetCursorPos(out POINT pt);

    [LibraryImport("user32.dll")]
    public static partial int SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [LibraryImport("shell32.dll")]
    public static partial int Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);

    [LibraryImport("user32.dll")]
    public static partial nint CreateIconFromResourceEx(nint presbits, uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);

    [LibraryImport("user32.dll")]
    public static partial int DestroyIcon(nint hIcon);
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public nint lpszMenuName;
    public nint lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public nint hIcon;
    public fixed char szTip[128];
    public uint dwState;
    public uint dwStateMask;
    public fixed char szInfo[256];
    public uint uVersionOrTimeout;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}
