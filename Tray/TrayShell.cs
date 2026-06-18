using System.Runtime.InteropServices;
using static Loudswitch.Tray.Win32;

namespace Loudswitch.Tray;

/// <summary>
/// WinForms 없는 트레이 셸: 숨은 top-level 창 + Shell_NotifyIcon + Win32 메시지 루프.
/// 트레이 클릭 시 <see cref="MenuRequested"/>로 컨트롤러가 <see cref="ShowMenu"/>를 호출해
/// 네이티브 팝업 메뉴를 즉석에서(동적) 띄운다. 아이콘은 <see cref="TrayIcons"/>(임베드 .ico → HICON)로 만든다.
/// </summary>
internal sealed unsafe class TrayShell : IDisposable
{
    private const uint TrayId = 1;
    private const nuint DefaultDeviceTimerId = 1;

    private static TrayShell? _instance; // WndProc(static)에서 인스턴스로 디스패치 (트레이는 단일)

    private readonly nint _hwnd;
    private readonly nint _activeIcon;   // HICON
    private readonly nint _inactiveIcon; // HICON
    private bool _active = true;
    private bool _disposed;

    /// <summary>트레이 좌/우클릭 → 컨트롤러가 메뉴를 빌드해 <see cref="ShowMenu"/> 호출.</summary>
    public Action? MenuRequested;

    /// <summary>기본 장치 추적 타이머 틱(메시지 루프 스레드).</summary>
    public Action? TimerTick;

    public TrayShell(string tooltip)
    {
        _instance = this;
        _activeIcon = TrayIcons.Create(active: true);
        _inactiveIcon = TrayIcons.Create(active: false);

        nint hInstance = GetModuleHandleW(null);
        nint classNamePtr = Marshal.StringToCoTaskMemUni("LoudswitchTrayWnd");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
            hInstance = hInstance,
            lpszClassName = classNamePtr,
        };
        RegisterClassExW(in wc);

        // 보이지 않는 일반 top-level 창(message-only가 아님 → 메뉴가 정상 닫힘)
        _hwnd = CreateWindowExW(0, "LoudswitchTrayWnd", "Loudswitch", 0, 0, 0, 0, 0, 0, 0, hInstance, 0);

        AddOrModifyIcon(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP, tooltip);
    }

    /// <summary>메시지 루프(블로킹). <see cref="Quit"/> 또는 창 파괴 시 반환.</summary>
    public void Run()
    {
        while (GetMessageW(out MSG msg, 0, 0, 0) > 0)
        {
            TranslateMessage(in msg);
            DispatchMessageW(in msg);
        }
    }

    public void Quit() => PostQuitMessage(0);

    public void StartDefaultDeviceTimer(uint intervalMs) => SetTimer(_hwnd, DefaultDeviceTimerId, intervalMs, 0);

    public void StopDefaultDeviceTimer() => KillTimer(_hwnd, DefaultDeviceTimerId);

    /// <summary>활성/비활성 아이콘 + 툴팁 갱신.</summary>
    public void SetActive(bool active, string tooltip)
    {
        _active = active;
        AddOrModifyIcon(NIM_MODIFY, NIF_ICON | NIF_TIP, tooltip);
    }

    /// <summary>콜백으로 메뉴를 빌드한 뒤 커서 위치에 띄우고, 선택된 항목의 액션을 실행한다.</summary>
    public void ShowMenu(Action<MenuBuilder> build)
    {
        var menu = new MenuBuilder();
        build(menu);

        GetCursorPos(out POINT pt);
        SetForegroundWindow(_hwnd); // TrackPopupMenu가 포커스 잃을 때 정상 닫히도록
        int cmd = TrackPopupMenu(menu.Handle, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.x, pt.y, 0, _hwnd, 0);

        Action? action = menu.ActionFor(cmd);
        menu.Dispose(); // 메뉴 파괴는 액션 실행 전에(액션이 새 메뉴/대화상자를 띄울 수 있음)
        action?.Invoke();
    }

    /// <summary>.exe 선택 대화상자. 선택된 전체 경로를 반환(취소 시 null).</summary>
    public string? BrowseForExecutable(string title)
    {
        const int maxChars = 260;
        nint fileBuf = Marshal.AllocCoTaskMem(maxChars * sizeof(char));
        nint filter = DoubleNull("실행 파일 (*.exe)", "*.exe");
        nint titlePtr = Marshal.StringToCoTaskMemUni(title);
        try
        {
            for (int i = 0; i < maxChars; i++)
                ((char*)fileBuf)[i] = '\0';

            var ofn = new OPENFILENAMEW
            {
                lStructSize = sizeof(OPENFILENAMEW),
                hwndOwner = _hwnd,
                lpstrFilter = filter,
                lpstrFile = fileBuf,
                nMaxFile = maxChars,
                lpstrTitle = titlePtr,
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_EXPLORER,
            };
            return GetOpenFileNameW(ref ofn) == 0 ? null : Marshal.PtrToStringUni(fileBuf);
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileBuf);
            Marshal.FreeCoTaskMem(filter);
            Marshal.FreeCoTaskMem(titlePtr);
        }
    }

    /// <summary>트레이 풍선 알림. Windows 10/11에선 토스트로 표시되어 알림 센터에 남는다.</summary>
    public void Notify(string title, string message)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = NIF_INFO | NIF_ICON,                 // hIcon을 함께 유효화
            hIcon = _active ? _activeIcon : _inactiveIcon, // 현재 트레이 아이콘 유지
            dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON,    // 알림 아이콘 = 위 hIcon
        };
        for (int i = 0; i < title.Length && i < 63; i++)
            data.szInfoTitle[i] = title[i];
        for (int i = 0; i < message.Length && i < 255; i++)
            data.szInfo[i] = message[i];

        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    public void Info(string text, string caption = "Loudswitch") =>
        MessageBoxW(_hwnd, text, caption, 0x40); // MB_ICONINFORMATION

    public void Warn(string text, string caption = "Loudswitch") =>
        MessageBoxW(_hwnd, text, caption, 0x30); // MB_ICONWARNING

    private void AddOrModifyIcon(uint message, uint flags, string tooltip)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = flags,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _active ? _activeIcon : _inactiveIcon,
        };
        for (int i = 0; i < tooltip.Length && i < 127; i++)
            data.szTip[i] = tooltip[i];

        Shell_NotifyIconW(message, ref data);
    }

    private static nint DoubleNull(params string[] parts) =>
        Marshal.StringToCoTaskMemUni(string.Join('\0', parts) + "\0\0");

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_APP_TRAY:
                    uint ev = (uint)(lParam & 0xFFFF);
                    if (ev is WM_RBUTTONUP or WM_LBUTTONUP or WM_CONTEXTMENU)
                        _instance?.MenuRequested?.Invoke();
                    return 0;
                case WM_TIMER:
                    _instance?.TimerTick?.Invoke();
                    return 0;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return 0;
            }
        }
        catch
        {
            // 네이티브 경계 밖으로 예외 전파 금지(프로세스 종료 방지).
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var data = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _hwnd, uID = TrayId };
        Shell_NotifyIconW(NIM_DELETE, ref data);

        if (_hwnd != 0)
            DestroyWindow(_hwnd);
        if (_activeIcon != 0)
            DestroyIcon(_activeIcon);
        if (_inactiveIcon != 0)
            DestroyIcon(_inactiveIcon);
    }
}

/// <summary>
/// 네이티브 팝업 메뉴(HMENU) 빌더. 즉석(immediate-mode)으로 항목을 추가하며 명령 ID↔액션을 매핑한다.
/// <see cref="Dispose"/>가 루트 메뉴를 재귀 파괴하고 할당 문자열을 해제한다.
/// </summary>
internal sealed class MenuBuilder : IDisposable
{
    public nint Handle { get; } = Win32.CreatePopupMenu();

    private readonly Dictionary<int, Action> _actions = new();
    private readonly List<nint> _strings = new();
    private int _nextId = 1;

    public MenuBuilder Label(string text)
    {
        Win32.AppendMenuW(Handle, Win32.MF_STRING | Win32.MF_GRAYED, 0, Str(text));
        return this;
    }

    public MenuBuilder Separator()
    {
        Win32.AppendMenuW(Handle, Win32.MF_SEPARATOR, 0, 0);
        return this;
    }

    public MenuBuilder Item(string text, Action onClick, bool enabled = true)
    {
        AddLeaf(Handle, text, onClick, isChecked: false, enabled);
        return this;
    }

    public MenuBuilder Check(string text, bool isChecked, Action onClick)
    {
        AddLeaf(Handle, text, onClick, isChecked, enabled: true);
        return this;
    }

    public MenuBuilder Submenu(string text, Action<SubMenu> build, bool enabled = true)
    {
        AddSubmenu(Handle, text, build, enabled);
        return this;
    }

    public Action? ActionFor(int cmd) => _actions.TryGetValue(cmd, out Action? a) ? a : null;

    // ── SubMenu가 호출하는 내부 헬퍼 (특정 메뉴 핸들에 추가) ──

    internal void AddLeaf(nint menu, string text, Action onClick, bool isChecked, bool enabled)
    {
        int id = _nextId++;
        _actions[id] = onClick;
        uint flags = Win32.MF_STRING | (isChecked ? Win32.MF_CHECKED : 0) | (enabled ? 0 : Win32.MF_GRAYED);
        Win32.AppendMenuW(menu, flags, id, Str(text));
    }

    internal void AddLabel(nint menu, string text) =>
        Win32.AppendMenuW(menu, Win32.MF_STRING | Win32.MF_GRAYED, 0, Str(text));

    internal void AddSeparator(nint menu) =>
        Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, 0);

    internal void AddSubmenu(nint parent, string text, Action<SubMenu> build, bool enabled)
    {
        nint sub = Win32.CreatePopupMenu();
        build(new SubMenu(this, sub));
        uint flags = Win32.MF_POPUP | Win32.MF_STRING | (enabled ? 0 : Win32.MF_GRAYED);
        Win32.AppendMenuW(parent, flags, sub, Str(text));
    }

    private nint Str(string s)
    {
        nint p = Marshal.StringToCoTaskMemUni(s);
        _strings.Add(p);
        return p;
    }

    public void Dispose()
    {
        Win32.DestroyMenu(Handle); // 서브메뉴까지 재귀 파괴
        foreach (nint p in _strings)
            Marshal.FreeCoTaskMem(p);
    }
}

/// <summary>서브메뉴에 항목을 추가하기 위한 얇은 뷰(빌더 상태 공유).</summary>
internal sealed class SubMenu
{
    private readonly MenuBuilder _owner;
    private readonly nint _menu;

    internal SubMenu(MenuBuilder owner, nint menu)
    {
        _owner = owner;
        _menu = menu;
    }

    public SubMenu Item(string text, Action onClick, bool enabled = true)
    {
        _owner.AddLeaf(_menu, text, onClick, isChecked: false, enabled);
        return this;
    }

    public SubMenu Check(string text, bool isChecked, Action onClick)
    {
        _owner.AddLeaf(_menu, text, onClick, isChecked, enabled: true);
        return this;
    }

    public SubMenu Label(string text)
    {
        _owner.AddLabel(_menu, text);
        return this;
    }

    public SubMenu Separator()
    {
        _owner.AddSeparator(_menu);
        return this;
    }

    public SubMenu Submenu(string text, Action<SubMenu> build, bool enabled = true)
    {
        _owner.AddSubmenu(_menu, text, build, enabled);
        return this;
    }
}
