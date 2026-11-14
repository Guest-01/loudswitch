using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Loudswitch.Interop;
using static Loudswitch.Tray.Win32;

namespace Loudswitch.Tray;

/// <summary>
/// WinForms 없는 트레이 셸: 숨은 top-level 창 + Shell_NotifyIcon + Win32 메시지 루프.
/// 트레이 클릭 시 <see cref="MenuRequested"/>로 컨트롤러가 <see cref="ShowMenu"/>를 호출해
/// 네이티브 팝업 메뉴를 즉석에서(동적) 띄운다. 아이콘은 <see cref="TrayIcons"/>(임베드 .ico → HICON)로 만든다.
///
/// Microsoft 문서의 알림 영역 규칙을 따른다:
/// <list type="bullet">
///   <item><c>NOTIFYICON_VERSION_4</c> 동작(<c>NIM_SETVERSION</c>) — 클릭은 NIN_SELECT, 우클릭·키보드 메뉴는
///         WM_CONTEXTMENU, 키보드 선택은 NIN_KEYSELECT로 오며 wParam에 메뉴 기준 좌표가 온다.</item>
///   <item>작업 표시줄이 다시 만들어지면(탐색기 재시작, 주 모니터 DPI 변경) 방송되는 <c>TaskbarCreated</c>를 받아
///         아이콘을 그 DPI에 맞게 다시 만들고 다시 등록한다.</item>
///   <item>알림 아이콘 메뉴는 <c>SetForegroundWindow</c> 후 띄우고, 닫힌 뒤 <c>WM_NULL</c>을 보내 다음 메뉴가
///         바로 닫히는 문제를 막는다(TrackPopupMenu 문서).</item>
/// </list>
/// </summary>
internal sealed unsafe class TrayShell : IDisposable
{
    private const uint TrayId = 1;

    private static TrayShell? _instance; // WndProc(static)에서 인스턴스로 디스패치 (트레이는 단일)
    private static uint _taskbarCreatedMessage; // RegisterWindowMessage("TaskbarCreated")

    private readonly nint _hwnd;
    private readonly ConcurrentQueue<Action> _posted = new(); // 다른 스레드 → 메시지 루프 스레드 작업함
    private nint _activeIcon;   // HICON (현재 DPI의 작은 아이콘)
    private nint _inactiveIcon; // HICON
    private nint _balloonIcon;  // HICON (알림 본문용 큰 아이콘)
    private string _tooltip;
    private bool _active = true;
    private bool _disposed;
    private POINT? _menuAnchor;          // 버전 4 콜백이 알려 준 메뉴 기준 좌표
    private long _menuClosedTicks;       // 메뉴가 닫힌 시각(연속 요청 무시용)

    /// <summary>트레이 클릭/키보드 선택 → 컨트롤러가 메뉴를 빌드해 <see cref="ShowMenu"/> 호출.</summary>
    public Action? MenuRequested;

    public TrayShell(string tooltip)
    {
        _instance = this;
        _tooltip = tooltip;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

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

        // 보이지 않는 일반 top-level 창(message-only가 아님 → 메뉴가 정상 닫히고 TaskbarCreated 방송도 받음)
        _hwnd = CreateWindowExW(0, "LoudswitchTrayWnd", "Loudswitch", 0, 0, 0, 0, 0, 0, 0, hInstance, 0);

        LoadIcons();
        RegisterIcon();
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

    /// <summary>
    /// 어느 스레드에서든 호출 가능. <paramref name="action"/>을 메시지 루프 스레드에서 실행하도록 넘긴다.
    /// 앱 상태는 메시지 루프 스레드만 만지게 해서(단일 스레드 모델) 잠금 없이 경합을 없앤다.
    /// 종료 후 Post된 작업은 조용히 버려진다.
    /// </summary>
    public void Post(Action action)
    {
        if (_disposed)
            return;
        _posted.Enqueue(action);
        PostMessageW(_hwnd, WM_APP_INVOKE, 0, 0);
    }

    private void RunPosted()
    {
        // 메시지 하나당 큐 전체를 비운다(PostMessage가 합쳐지거나 실패해도 다음 메시지에서 처리됨).
        while (_posted.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch
            {
                // 한 작업의 실패가 나머지 작업과 메시지 루프를 막지 않도록.
            }
        }
    }

    /// <summary>활성/비활성 아이콘 + 툴팁 갱신.</summary>
    public void SetActive(bool active, string tooltip)
    {
        _active = active;
        _tooltip = tooltip;
        NOTIFYICONDATAW data = Fill(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>콜백으로 메뉴를 빌드한 뒤 아이콘 옆(또는 커서 위치)에 띄우고, 선택된 항목의 액션을 실행한다.</summary>
    public void ShowMenu(Action<MenuBuilder> build)
    {
        var menu = new MenuBuilder();
        build(menu);

        POINT pt;
        if (_menuAnchor is POINT anchor)
            pt = anchor;
        else
            GetCursorPos(out pt);
        _menuAnchor = null;

        // 시스템 설정(SM_MENUDROPALIGNMENT)에 맞춰 메뉴를 기준점의 왼쪽/오른쪽으로 연다(TrackPopupMenu 문서 권장).
        uint align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;

        SetForegroundWindow(_hwnd); // 바깥을 클릭하면 메뉴가 닫히도록(문서 요구사항)
        int cmd = TrackPopupMenu(menu.Handle, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY | align, pt.x, pt.y, 0, _hwnd, 0);
        PostMessageW(_hwnd, WM_NULL, 0, 0); // 다음 메뉴가 열리자마자 닫히는 문제 방지(문서 요구사항)
        _menuClosedTicks = Environment.TickCount64;

        Action? action = menu.ActionFor(cmd);
        menu.Dispose(); // 메뉴 파괴는 액션 실행 전에(액션이 새 메뉴/대화상자를 띄울 수 있음)
        action?.Invoke();
    }

    /// <summary>.exe 선택 대화상자(Common Item Dialog). 선택된 전체 경로를 반환(취소 시 null).</summary>
    public string? BrowseForExecutable(string title)
    {
        try
        {
            return FileOpenDialog.PickFile(_hwnd, title, "실행 파일 (*.exe)", "*.exe");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>트레이 알림. Windows 10/11에선 토스트로 표시되어 알림 센터에 남는다.</summary>
    public void Notify(string title, string message)
    {
        NOTIFYICONDATAW data = Fill(NIF_INFO | NIF_ICON);
        data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON; // 알림 아이콘 = hBalloonIcon(큰 아이콘)
        data.hBalloonIcon = _balloonIcon;
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

    /// <summary>현재 DPI에 맞는 아이콘을 (다시) 만든다. 이전 핸들은 등록을 바꾼 뒤 해제한다.</summary>
    private void LoadIcons()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        if (dpi == 0)
            dpi = 96;

        nint oldActive = _activeIcon, oldInactive = _inactiveIcon, oldBalloon = _balloonIcon;
        _activeIcon = TrayIcons.CreateSmall(active: true, dpi);
        _inactiveIcon = TrayIcons.CreateSmall(active: false, dpi);
        _balloonIcon = TrayIcons.CreateLarge(active: true, dpi);

        foreach (nint h in new[] { oldActive, oldInactive, oldBalloon })
        {
            if (h != 0)
                DestroyIcon(h);
        }
    }

    /// <summary>아이콘을 알림 영역에 추가하고 버전 4 동작을 요청한다(처음 실행과 작업 표시줄 재생성 때).</summary>
    private void RegisterIcon()
    {
        NOTIFYICONDATAW data = Fill(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_ADD, ref data);
        data.uVersionOrTimeout = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    /// <summary>작업 표시줄이 다시 만들어짐 → 기존 아이콘은 사라졌다고 보고 새 DPI로 다시 등록(문서 요구사항).</summary>
    private void OnTaskbarCreated()
    {
        LoadIcons();
        RegisterIcon();
    }

    private void OnTrayCallback(nint wParam, nint lParam)
    {
        uint ev = (uint)(lParam & 0xFFFF);
        if (ev is not (NIN_SELECT or NIN_KEYSELECT or WM_CONTEXTMENU))
            return;

        // 키보드 Enter는 NIN_KEYSELECT가 연달아 올 수 있다 → 메뉴가 막 닫힌 직후의 요청은 무시.
        if (Environment.TickCount64 - _menuClosedTicks < 300)
            return;

        // 버전 4: wParam 하위/상위 16비트 = 메뉴 기준 화면 좌표(부호 있음).
        _menuAnchor = new POINT { x = (short)(wParam & 0xFFFF), y = (short)((wParam >> 16) & 0xFFFF) };
        MenuRequested?.Invoke();
    }

    private NOTIFYICONDATAW Fill(uint flags)
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
        string tip = _tooltip;
        for (int i = 0; i < tip.Length && i < 127; i++)
            data.szTip[i] = tip[i];
        return data;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == _taskbarCreatedMessage && msg != 0)
            {
                _instance?.OnTaskbarCreated();
                return 0;
            }

            switch (msg)
            {
                case WM_APP_TRAY:
                    _instance?.OnTrayCallback(wParam, lParam);
                    return 0;
                case WM_APP_INVOKE:
                    _instance?.RunPosted();
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
        foreach (nint h in new[] { _activeIcon, _inactiveIcon, _balloonIcon })
        {
            if (h != 0)
                DestroyIcon(h);
        }
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
