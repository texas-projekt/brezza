using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Brezza.App.Native;
using Brezza.Core.Abstractions;
using Brezza.Core.Navigation;
using Brezza.Core.Tabs;

namespace Brezza.App;

/// <summary>
/// The native browser window: a real top-level HWND whose tab strip, toolbar, address field,
/// navigation buttons, and notice area are standard Win32 controls. The window renders state it is
/// given and raises intents; it never owns browser state and never touches WebView2 objects.
/// </summary>
/// <remarks>
/// Layout, top to bottom: a caption row (tab strip, new-tab actions, drag area, window buttons),
/// the toolbar (navigation, address field, page and panel actions), a hairline divider, an optional
/// notice bar, and the content area that hosts the WebView2 surface or the native new-tab page.
/// Buttons are owner-drawn through <see cref="FlatButtonSet"/>; the tab strip is painted here.
/// </remarks>
internal sealed class MainWindow : IDisposable
{
    public const string WindowClassName = "BrezzaMainWindow";

    public const int IdTabs = 1001;
    public const int IdNewTab = 1002;
    public const int IdNewPrivateTab = 1003;
    public const int IdCloseActiveTab = 1004;
    public const int IdBack = 1010;
    public const int IdForward = 1011;
    public const int IdReload = 1012;
    public const int IdAddress = 1013;
    public const int IdBookmark = 1014;
    public const int IdHistory = 1015;
    public const int IdBookmarks = 1016;
    public const int IdSettings = 1017;
    public const int IdMenu = 1018;
    public const int IdNotice = 1020;
    public const int IdNoticeAction = 1021;
    public const int IdNoticeDismiss = 1022;
    public const int IdStartTitle = 1023;
    public const int IdStartSubtitle = 1024;
    public const int IdTitleMinimize = 1031;
    public const int IdTitleMaximize = 1032;
    public const int IdTitleClose = 1033;
    private const int SleepTimerId = 1;
    private const int ShutdownGuardTimerId = 3;

    private const int MaximumTabTextLength = 80;
    private const int MinimumWindowWidth = 640;
    private const int MinimumWindowHeight = 420;

    // Unscaled metrics at 96 DPI.
    private const int CaptionHeight = 40;
    private const int TabTop = 6;
    private const int ToolbarHeight = 44;
    private const int NoticeHeight = 40;
    private const int ControlSize = 32;
    private const int CaptionButtonWidth = 46;
    private const int CaptionButtonHeight = 32;
    private const int MinimumTabWidth = 64;
    private const int MaximumTabWidth = 240;

    // Icon font glyphs (Segoe Fluent Icons, with Segoe MDL2 Assets code points).
    private const string GlyphBack = "\uE72B";
    private const string GlyphForward = "\uE72A";
    private const string GlyphReload = "\uE72C";
    private const string GlyphStar = "\uE734";
    private const string GlyphStarFilled = "\uE735";
    private const string GlyphMore = "\uE712";
    private const string GlyphAdd = "\uE710";
    // An eye with a slash: browsing that is not seen or recorded.
    private const string GlyphPrivate = "\uED1A";
    private const string GlyphClose = "\uE711";
    private const string GlyphMinimize = "\uE921";
    private const string GlyphMaximize = "\uE922";
    private const string GlyphRestore = "\uE923";
    private const string GlyphCaptionClose = "\uE8BB";

    private readonly NativeMethods.WndProc _wndProc;
    private readonly NativeMethods.SubClassProc _tabStripSubClass;
    private readonly NativeMethods.SubClassProc _addressEditSubClass;
    private readonly Localization _localization;
    private readonly NativeThemeResources _theme = new();
    private readonly NativeFonts _fonts = new();
    private readonly FlatButtonSet _buttons;
    private readonly FlatFieldSet _fields;
    private readonly FlatPopup _menu;

    private IntPtr _hwnd;
    private uint _dpi = NativeMethods.USER_DEFAULT_SCREEN_DPI;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    private IntPtr _tabStrip;
    private IntPtr _newTabButton;
    private IntPtr _closeActiveTabButton;
    private IntPtr _backButton;
    private IntPtr _forwardButton;
    private IntPtr _reloadButton;
    private IntPtr _addressCombo;
    private IntPtr _addressEdit;
    private IntPtr _bookmarkButton;
    private IntPtr _menuButton;
    private IntPtr _noticeText;
    private IntPtr _noticeActionButton;
    private IntPtr _noticeDismissButton;
    private IntPtr _startTitleText;
    private IntPtr _startSubtitleText;
    private IntPtr _titleMinimize;
    private IntPtr _titleMaximize;
    private IntPtr _titleClose;

    private readonly List<(string Id, string Text, bool Private)> _tabMirror = new();
    private int _hotTab = -1;
    private int _laidOutTabCount = -1;
    private string _currentUrl = string.Empty;
    private bool _noticeVisible;
    private bool _bookmarked;
    private bool _startPageVisible;
    private string _startTitle = string.Empty;
    private string _startSubtitle = string.Empty;

    public MainWindow(Localization localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _wndProc = OnWindowMessage;
        _tabStripSubClass = OnTabStripMessage;
        _addressEditSubClass = OnAddressEditMessage;
        _buttons = new FlatButtonSet(_theme, _fonts, () => _dpi);
        _fields = new FlatFieldSet(_theme, _fonts, () => _dpi);
        _menu = new FlatPopup(_theme, _fonts, () => _dpi);
        _startPageVisible = true;
        _startTitle = _localization.Get("app.name");
        _startSubtitle = _localization.Get("start.subtitle");
    }

    // ---------- Intents raised by native controls ----------

    public event EventHandler? CloseRequested;
    public event EventHandler<string>? NavigateRequested;
    public event EventHandler<string>? ActivateTabRequested;
    public event EventHandler<string>? CloseTabRequested;
    public event EventHandler? NewTabRequested;
    public event EventHandler? NewPrivateTabRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? ForwardRequested;
    public event EventHandler? ReloadRequested;
    public event EventHandler? ToggleBookmarkRequested;
    public event EventHandler<string>? OpenPanelRequested;
    public event EventHandler? NoticeDismissRequested;
    public event EventHandler? NoticeReloadRequested;
    public event EventHandler<BrowserShortcutEventArgs>? ShortcutPressed;
    public event EventHandler<int>? TimerTick;
    public event EventHandler<ContentBounds>? ContentBoundsChanged;

    /// <summary>Supplies address-field suggestions when the user opens the drop-down.</summary>
    public Func<IReadOnlyList<string>>? AddressSuggestionsProvider { get; set; }

    public IntPtr Handle => _hwnd;

    // ---------- Lifetime ----------

    public void Create()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            hIcon = NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512)),
            hCursor = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(32512)),
            hbrBackground = IntPtr.Zero,
            lpszClassName = WindowClassName,
        };

        if (NativeMethods.RegisterClassEx(ref wndClass) == 0 &&
            Marshal.GetLastWin32Error() != NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
        {
            throw new InvalidOperationException(
                "The browser window class could not be registered (error " + Marshal.GetLastWin32Error() + ").");
        }

        // A standard overlapped window keeps snap, the system menu, and the invisible resize borders;
        // WM_NCCALCSIZE removes only the system caption so the tab row can take its place.
        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            _localization.Get("app.name"),
            NativeMethods.WS_OVERLAPPEDWINDOW | NativeMethods.WS_CLIPCHILDREN,
            unchecked((int)0x80000000),
            unchecked((int)0x80000000),
            1120,
            760,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The browser window could not be created (error " + Marshal.GetLastWin32Error() + ").");
        }

        // Start from the DPI of the monitor the window landed on; WM_DPICHANGED keeps it current.
        var windowDpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (windowDpi > 0) _dpi = windowDpi;
        ApplyFonts();
        ApplyTheme(_theme.Mode);

        // WM_CREATE runs before CreateWindowEx returns the top-level HWND, so the first
        // language pass is intentionally guarded. Apply it again once all handles exist.
        ApplyLanguage();
    }

    public void Show()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        NativeMethods.UpdateWindow(_hwnd);
    }

    public int RunMessageLoop()
    {
        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (IsKeyDown(message.message) && FlatPopup.Current is { } popup &&
                popup.TryHandleKey(NativeMethods.GetLoWord(message.wParam))) continue;
            if (TryHandleKey(message.message, message.wParam, message.lParam)) continue;
            if (DialogKeyboard.TryTranslate(ref message)) continue;
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        return 0;
    }

    /// <summary>Destroys the window after the shell finished its asynchronous shutdown.</summary>
    public void CompleteShutdown()
    {
        if (_hwnd == IntPtr.Zero) return;
        _shutdownCompleted = true;
        NativeMethods.KillTimer(_hwnd, new IntPtr(ShutdownGuardTimerId));
        NativeMethods.DestroyWindow(_hwnd);
    }

    public void Dispose()
    {
        if (_tabStrip != IntPtr.Zero)
        {
            NativeMethods.RemoveWindowSubclass(_tabStrip, _tabStripSubClass, new UIntPtr(1));
        }

        _menu.Dispose();
        _buttons.Dispose();
        _fields.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _fonts.Dispose();
        _theme.Dispose();
    }

    // ---------- State rendering ----------

    /// <summary>Applies localized labels, accessible names, and tooltips, then re-lays out the window.</summary>
    public void ApplyLanguage()
    {
        if (_hwnd == IntPtr.Zero) return;

        // Icon buttons keep their localized label as window text so assistive technology can name them.
        Label(_newTabButton, "tab.newAction", "tab.newActionShortcut");
        Label(_closeActiveTabButton, "tab.close", "tab.closeShortcut");
        Label(_backButton, "toolbar.back", "toolbar.backShortcut");
        Label(_forwardButton, "toolbar.forward", "toolbar.forwardShortcut");
        Label(_reloadButton, "toolbar.reload", "toolbar.reloadShortcut");
        Label(_menuButton, "toolbar.menu", "toolbar.menuShortcut");
        Label(_titleMinimize, "window.minimize", "window.minimize");
        Label(_titleClose, "window.close", "window.close");
        UpdateMaximizeButton();
        SetBookmarked(_bookmarked);
        NativeMethods.SetWindowText(_noticeActionButton, _localization.Get("notice.reload"));
        NativeMethods.SetWindowText(_noticeDismissButton, _localization.Get("notice.dismiss"));
        NativeMethods.SetWindowText(_startTitleText, _startTitle);
        NativeMethods.SetWindowText(_startSubtitleText, _startSubtitle);

        // The address field has no placeholder property, so the cue banner carries the label.
        NativeMethods.SendMessage(
            _addressEdit != IntPtr.Zero ? _addressEdit : _addressCombo,
            0x1501, // EM_SETCUEBANNER
            IntPtr.Zero,
            _localization.Get("address.placeholder"));

        Layout();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    public void ApplyTheme(BrowserThemeMode mode)
    {
        _theme.Apply(mode);
        if (_hwnd == IntPtr.Zero) return;

        _theme.ApplyToWindowFrame(_hwnd);
        var tabTooltips = NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_GETTOOLTIPS, IntPtr.Zero, IntPtr.Zero);
        _theme.ApplyToControl(tabTooltips);
        _buttons.Refresh();
        _fields.Refresh(Scale(ControlSize));
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
        Layout();
    }

    public void UpdateTabs(IReadOnlyList<BrowserTab> tabs, string? activeTabId, bool activeLoading = false)
    {
        if (_hwnd == IntPtr.Zero || _tabStrip == IntPtr.Zero) return;

        var desired = tabs
            .Select(tab => (tab.Id, Text: TabText(tab, tab.Id == activeTabId && activeLoading),
                Private: tab.SessionKind == BrowserSessionKind.Private))
            .ToList();
        // Make room for the new count first, so inserting a tab never overflows the strip.
        var countChanged = desired.Count != _laidOutTabCount;
        if (countChanged)
        {
            NativeMethods.GetClientRect(_hwnd, out var client);
            SizeTabStrip(client.Width, desired.Count);
        }

        NativeMethods.SendMessage(_tabStrip, NativeMethods.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            var sameOrder = _tabMirror.Count == desired.Count &&
                _tabMirror.Select(item => item.Id).SequenceEqual(desired.Select(item => item.Id));
            if (!sameOrder)
            {
                NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_DELETEALLITEMS, IntPtr.Zero, IntPtr.Zero);
                for (var index = 0; index < desired.Count; index++)
                {
                    InsertTab(index, desired[index].Text);
                }
            }
            else
            {
                for (var index = 0; index < desired.Count; index++)
                {
                    if (!string.Equals(_tabMirror[index].Text, desired[index].Text, StringComparison.Ordinal))
                        SetTabText(index, desired[index].Text);
                }
            }

            _tabMirror.Clear();
            _tabMirror.AddRange(desired);

            var activeIndex = tabs.Select((tab, index) => (tab, index)).FirstOrDefault(pair => pair.tab.Id == activeTabId).index;
            NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_SETCURSEL, new IntPtr(activeIndex), IntPtr.Zero);
        }
        finally
        {
            NativeMethods.SendMessage(_tabStrip, NativeMethods.WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
        }

        // The strip was sized above; on a count change the new-tab button follows it. Otherwise only
        // the close button follows the selection, which keeps title updates cheap.
        if (countChanged) Layout();
        else PositionTabClose();
        NativeMethods.InvalidateRect(_tabStrip, IntPtr.Zero, false);
    }

    public void UpdateNavigationState(bool canGoBack, bool canGoForward)
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.EnableWindow(_backButton, canGoBack);
        NativeMethods.EnableWindow(_forwardButton, canGoForward);
        NativeMethods.EnableWindow(_reloadButton, _currentUrl.Length > 0);
    }

    public void SetBookmarked(bool bookmarked)
    {
        _bookmarked = bookmarked;
        if (_hwnd == IntPtr.Zero) return;
        var key = bookmarked ? "toolbar.removeBookmark" : "toolbar.bookmark";
        Label(_bookmarkButton, key, key);
        _buttons.SetGlyph(_bookmarkButton, bookmarked ? GlyphStarFilled : GlyphStar);
    }

    public void SetAddressText(string? text)
    {
        _currentUrl = text ?? string.Empty;
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowText(_addressCombo, _currentUrl);
    }

    public void SetWindowTitle(string title)
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowText(_hwnd, title);
    }

    public void FocusAddress()
    {
        if (_hwnd == IntPtr.Zero || _addressCombo == IntPtr.Zero) return;
        var target = _addressEdit != IntPtr.Zero ? _addressEdit : _addressCombo;
        NativeMethods.SetFocus(target);
        NativeMethods.SendMessage(target, NativeMethods.EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
    }

    public void SetNotice(string? key, string? detail)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (string.IsNullOrEmpty(key))
        {
            _noticeVisible = false;
            ShowControls(false, _noticeText, _noticeActionButton, _noticeDismissButton);
            Layout();
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
            return;
        }

        var text = _localization.Get(key!);
        if (!string.IsNullOrEmpty(detail)) text += " (" + detail + ")";
        NativeMethods.SetWindowText(_noticeText, text);

        // Only recoverable failures offer a reload action.
        var canReload = string.Equals(key, "notice.pageStopped", StringComparison.Ordinal) ||
            string.Equals(key, "notice.navigationFailed", StringComparison.Ordinal);
        ShowControls(canReload, _noticeActionButton);
        ShowControls(true, _noticeText, _noticeDismissButton);
        _noticeVisible = true;
        Layout();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    /// <summary>Shows the browser-owned new-tab page while the active tab has no content host.</summary>
    public void SetNewTabPage(bool visible, bool isPrivate)
    {
        _startPageVisible = visible;
        _startTitle = _localization.Get(isPrivate ? "start.privateTitle" : "app.name");
        _startSubtitle = _localization.Get(isPrivate ? "start.privateBody" : "start.subtitle");
        RenderStartPage();
    }

    /// <summary>Reports a startup failure in place of the new-tab page.</summary>
    public void SetStartupError(string message)
    {
        _startTitle = _localization.Get("app.name");
        _startSubtitle = message ?? string.Empty;
        _startPageVisible = true;
        RenderStartPage();
    }

    public bool IsNewTabPageVisible => _startPageVisible;

    public bool IsNoticeVisible => _noticeVisible;

    public string WindowTitle => _hwnd == IntPtr.Zero ? string.Empty : NativeMethods.GetWindowText(_hwnd);

    public string NoticeText => _noticeText == IntPtr.Zero ? string.Empty : NativeMethods.GetWindowText(_noticeText);

    public string AddressText => _addressCombo == IntPtr.Zero ? string.Empty : NativeMethods.GetWindowText(_addressCombo);

    /// <summary>The handle of a native control by its control id.</summary>
    public IntPtr GetControl(int controlId) =>
        _hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetDlgItem(_hwnd, controlId);

    public bool IsControlVisible(int controlId)
    {
        var control = GetControl(controlId);
        return control != IntPtr.Zero && NativeMethods.IsWindowVisible(control);
    }

    public bool IsControlEnabled(int controlId)
    {
        var control = GetControl(controlId);
        return control != IntPtr.Zero && NativeMethods.IsWindowEnabled(control);
    }

    public string GetControlText(int controlId)
    {
        var control = GetControl(controlId);
        return control == IntPtr.Zero ? string.Empty : NativeMethods.GetWindowText(control);
    }

    public int TabCount => _tabStrip == IntPtr.Zero
        ? 0
        : (int)NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);

    public int SelectedTabIndex => _tabStrip == IntPtr.Zero
        ? -1
        : (int)NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_GETCURSEL, IntPtr.Zero, IntPtr.Zero);

    public string GetTabText(int index)
    {
        if (_tabStrip == IntPtr.Zero) return string.Empty;
        return NativeMethods.GetTabText(_tabStrip, index);
    }

    public ContentBounds GetContentBounds()
    {
        if (_hwnd == IntPtr.Zero) return default;
        NativeMethods.GetClientRect(_hwnd, out var client);
        var top = ContentTop();
        return new ContentBounds(0, top, client.Width, Math.Max(0, client.Height - top));
    }

    /// <summary>The window's DPI as a WebView2 rasterization scale (1.0 at 96 DPI).</summary>
    public double ContentScale => _dpi / (double)NativeMethods.USER_DEFAULT_SCREEN_DPI;

    // ---------- Window procedure ----------

    private IntPtr OnWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case NativeMethods.WM_CREATE:
                    CreateControls(hWnd);
                    return IntPtr.Zero;

                case NativeMethods.WM_NCCALCSIZE when wParam != IntPtr.Zero:
                    return OnNcCalcSize(hWnd, msg, wParam, lParam);

                case NativeMethods.WM_ACTIVATE when NativeMethods.GetLoWord(wParam) == NativeMethods.WA_INACTIVE:
                case NativeMethods.WM_MOVE:
                    // A pop-up belongs to the moment it was opened in: switching apps or moving the
                    // window dismisses it, as with system menus.
                    FlatPopup.Current?.Close();
                    break;

                case NativeMethods.WM_SIZE:
                    // A pop-up anchored to the old layout must not float over the new one.
                    FlatPopup.Current?.Close();
                    UpdateMaximizeButton();
                    Layout();
                    return IntPtr.Zero;

                case NativeMethods.WM_NCHITTEST:
                    return HitTest(hWnd, msg, wParam, lParam);

                case NativeMethods.WM_ERASEBKGND:
                    return new IntPtr(1);

                case NativeMethods.WM_PAINT:
                    return OnPaint(hWnd);

                case NativeMethods.WM_DPICHANGED:
                    ApplyDpiChange((uint)(wParam.ToInt64() & 0xFFFF), lParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_SETTINGCHANGE:
                    if (_theme.Mode == BrowserThemeMode.System) ApplyTheme(_theme.Mode);
                    return IntPtr.Zero;

                case NativeMethods.WM_COMMAND:
                    OnCommand(wParam, lParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_DRAWITEM:
                    return _buttons.TryDraw(lParam) || _fields.TryDraw(lParam) ? new IntPtr(1) : IntPtr.Zero;

                case NativeMethods.WM_MEASUREITEM:
                    return _fields.TryMeasure(lParam) ? new IntPtr(1) : IntPtr.Zero;

                case NativeMethods.WM_NOTIFY:
                    OnNotify(lParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_TIMER:
                    TimerTick?.Invoke(this, NativeMethods.GetLoWord(wParam));
                    return IntPtr.Zero;

                case NativeMethods.WM_CTLCOLORSTATIC:
                    return OnStaticColor(wParam, lParam);

                case NativeMethods.WM_CTLCOLOREDIT:
                case NativeMethods.WM_CTLCOLORLISTBOX:
                    if (_fields.TryGetColors(lParam, wParam, out var fieldBrush)) return fieldBrush;
                    break;

                case NativeMethods.WM_GETMINMAXINFO:
                    OnMinMaxInfo(lParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_CLOSE:
                    OnClose();
                    return IntPtr.Zero;

                case NativeMethods.WM_DESTROY:
                    NativeMethods.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception exception)
        {
            // A native window procedure must never let an exception escape into the OS.
            Trace.TraceError("The browser window message {0} failed: {1}", msg, exception);
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnClose()
    {
        if (_shutdownCompleted)
        {
            NativeMethods.DestroyWindow(_hwnd);
            return;
        }

        if (_shutdownStarted)
        {
            // A second close request during shutdown forces the window away.
            Trace.TraceWarning("The browser was closed again while shutting down.");
            CompleteShutdown();
            return;
        }

        _shutdownStarted = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);

        // The shell finishes shutdown asynchronously; this guard keeps a stuck shutdown observable.
        NativeMethods.SetTimer(_hwnd, new IntPtr(ShutdownGuardTimerId), 20000, IntPtr.Zero);
    }

    /// <summary>
    /// Keeps the standard left, right, and bottom frame (the invisible resize borders) and removes the
    /// system caption. A maximized window extends past the monitor by its frame, so that part is trimmed.
    /// </summary>
    private IntPtr OnNcCalcSize(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var proposed = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
        var top = proposed.top;
        var result = NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        var client = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
        client.top = top;
        if (NativeMethods.IsZoomed(hWnd))
        {
            client.top += NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYFRAME, _dpi) +
                NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXPADDEDBORDER, _dpi);
        }

        Marshal.StructureToPtr(client, lParam, false);
        return result;
    }

    private IntPtr HitTest(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var hit = NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        if (hit.ToInt64() != NativeMethods.HTCLIENT) return hit;

        var point = new NativeMethods.POINT
        {
            x = unchecked((short)NativeMethods.GetLoWord(lParam)),
            y = unchecked((short)NativeMethods.GetHiWord(lParam)),
        };
        NativeMethods.ScreenToClient(_hwnd, ref point);
        if (!NativeMethods.IsZoomed(hWnd) && point.y >= 0 && point.y < Scale(5)) return new IntPtr(NativeMethods.HTTOP);

        // Only the empty part of the caption row reaches the parent; controls answer for themselves.
        return point.y >= 0 && point.y < Scale(CaptionHeight)
            ? new IntPtr(NativeMethods.HTCAPTION)
            : new IntPtr(NativeMethods.HTCLIENT);
    }

    private IntPtr OnPaint(IntPtr hWnd)
    {
        var paint = new NativeMethods.PAINTSTRUCT();
        var dc = NativeMethods.BeginPaint(hWnd, ref paint);
        try
        {
            NativeMethods.GetClientRect(hWnd, out var client);
            var palette = _theme.Palette;
            var caption = Scale(CaptionHeight);
            var toolbarBottom = caption + Scale(ToolbarHeight);
            NativePaint.Fill(dc, NativePaint.Rect(0, 0, client.Width, caption), palette.Frame, _theme);
            NativePaint.Fill(dc, NativePaint.Rect(0, caption, client.Width, Scale(ToolbarHeight)), palette.Surface, _theme);
            NativePaint.Fill(dc, NativePaint.Rect(0, toolbarBottom, client.Width, 1), palette.Border, _theme);
            var contentTop = toolbarBottom + 1;
            if (_noticeVisible)
            {
                NativePaint.Fill(dc, NativePaint.Rect(0, contentTop, client.Width, Scale(NoticeHeight)), palette.Notice, _theme);
                contentTop += Scale(NoticeHeight);
            }

            // The content area shows the new-tab page; a WebView2 surface covers it when a page is hosted.
            NativePaint.Fill(dc, NativePaint.Rect(0, contentTop, client.Width, client.Height - contentTop), palette.Surface, _theme);
        }
        finally
        {
            NativeMethods.EndPaint(hWnd, ref paint);
        }

        return IntPtr.Zero;
    }

    private IntPtr OnStaticColor(IntPtr dc, IntPtr control)
    {
        var palette = _theme.Palette;
        var (foreground, background) =
            control == _noticeText ? (palette.NoticeText, palette.Notice) :
            control == _startSubtitleText ? (palette.SecondaryText, palette.Surface) :
            (palette.Text, palette.Surface);
        NativeMethods.SetTextColor(dc, foreground);
        NativeMethods.SetBkColor(dc, background);
        return _theme.Brush(background);
    }

    private void OnMinMaxInfo(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;
        var info = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        info.ptMinTrackSize = new NativeMethods.POINT { x = Scale(MinimumWindowWidth), y = Scale(MinimumWindowHeight) };
        Marshal.StructureToPtr(info, lParam, false);
    }

    private void ApplyDpiChange(uint dpi, IntPtr suggestedRect)
    {
        _dpi = dpi == 0 ? NativeMethods.USER_DEFAULT_SCREEN_DPI : dpi;
        ApplyFonts();
        if (suggestedRect != IntPtr.Zero)
        {
            var rect = Marshal.PtrToStructure<NativeMethods.RECT>(suggestedRect);
            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                rect.left,
                rect.top,
                rect.Width,
                rect.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        ApplyLanguage();
    }

    // ---------- Controls ----------

    private void CreateControls(IntPtr hWnd)
    {
        const uint child = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
        _tabStrip = CreateChild(hWnd, "SysTabControl32", IdTabs,
            child | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.TCS_TABS | NativeMethods.TCS_SINGLELINE |
            NativeMethods.TCS_TOOLTIPS | NativeMethods.TCS_FIXEDWIDTH | NativeMethods.TCS_FOCUSNEVER);
        _closeActiveTabButton = CreateButton(hWnd, IdCloseActiveTab, FlatButtonStyle.SmallIcon, FlatBackdrop.Surface, GlyphClose);
        _newTabButton = CreateButton(hWnd, IdNewTab, FlatButtonStyle.Icon, FlatBackdrop.Frame, GlyphAdd);
        _titleMinimize = CreateButton(hWnd, IdTitleMinimize, FlatButtonStyle.Caption, FlatBackdrop.Frame, GlyphMinimize);
        _titleMaximize = CreateButton(hWnd, IdTitleMaximize, FlatButtonStyle.Caption, FlatBackdrop.Frame, GlyphMaximize);
        _titleClose = CreateButton(hWnd, IdTitleClose, FlatButtonStyle.CaptionClose, FlatBackdrop.Frame, GlyphCaptionClose);

        _backButton = CreateButton(hWnd, IdBack, FlatButtonStyle.Icon, FlatBackdrop.Surface, GlyphBack);
        _forwardButton = CreateButton(hWnd, IdForward, FlatButtonStyle.Icon, FlatBackdrop.Surface, GlyphForward);
        _reloadButton = CreateButton(hWnd, IdReload, FlatButtonStyle.Icon, FlatBackdrop.Surface, GlyphReload);
        _addressCombo = CreateChild(hWnd, "COMBOBOX", IdAddress,
            FlatFieldSet.EditableListStyle | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS);
        _bookmarkButton = CreateButton(hWnd, IdBookmark, FlatButtonStyle.Icon, FlatBackdrop.Surface, GlyphStar);
        _menuButton = CreateButton(hWnd, IdMenu, FlatButtonStyle.Icon, FlatBackdrop.Surface, GlyphMore);

        _noticeText = CreateChild(hWnd, "STATIC", IdNotice,
            NativeMethods.WS_CHILD | NativeMethods.SS_LEFTNOWORDWRAP | NativeMethods.SS_ENDELLIPSIS | NativeMethods.SS_NOPREFIX);
        _noticeActionButton = CreateButton(hWnd, IdNoticeAction, FlatButtonStyle.Subtle, FlatBackdrop.Notice, visible: false);
        _noticeDismissButton = CreateButton(hWnd, IdNoticeDismiss, FlatButtonStyle.Subtle, FlatBackdrop.Notice, visible: false);
        _startTitleText = CreateChild(hWnd, "STATIC", IdStartTitle,
            NativeMethods.WS_CHILD | NativeMethods.SS_CENTER | NativeMethods.SS_NOPREFIX);
        _startSubtitleText = CreateChild(hWnd, "STATIC", IdStartSubtitle,
            NativeMethods.WS_CHILD | NativeMethods.SS_CENTER | NativeMethods.SS_NOPREFIX);

        NativeMethods.SendMessage(_addressCombo, NativeMethods.CB_LIMITTEXT, IntPtr.Zero, new IntPtr(8192));

        // The close button floats over the active tab, so it must stay above the strip.
        NativeMethods.SetWindowPos(_closeActiveTabButton, NativeMethods.HWND_TOP, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        // Painting, hover, middle-click close, and wheel switching for the tab strip.
        NativeMethods.SetWindowSubclass(_tabStrip, _tabStripSubClass, new UIntPtr(1), IntPtr.Zero);

        _addressEdit = NativeMethods.FindWindowEx(_addressCombo, IntPtr.Zero, "EDIT", null);
        _fields.RegisterCombo(_addressCombo, FlatBackdrop.Surface);
        if (_addressEdit != IntPtr.Zero)
        {
            NativeMethods.SetWindowSubclass(_addressEdit, _addressEditSubClass, new UIntPtr(1), IntPtr.Zero);
        }

        _noticeVisible = false;
    }

    private IntPtr CreateButton(IntPtr parent, int controlId, FlatButtonStyle style, FlatBackdrop backdrop,
        string? glyph = null, bool visible = true)
    {
        var button = CreateChild(parent, "BUTTON", controlId,
            NativeMethods.WS_CHILD | (visible ? NativeMethods.WS_VISIBLE : 0) | NativeMethods.BS_OWNERDRAW);
        _buttons.Register(button, style, backdrop, glyph);
        return button;
    }

    private IntPtr CreateChild(IntPtr parent, string className, int controlId, uint style)
    {
        if (className == "BUTTON" || className == "EDIT" || className == "COMBOBOX" || className == "LISTBOX")
            style |= NativeMethods.WS_TABSTOP;
        var handle = NativeMethods.CreateWindowEx(
            0,
            className,
            string.Empty,
            style,
            0, 0, 0, 0,
            parent,
            new IntPtr(controlId),
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The native control " + className + " (" + controlId + ") could not be created (error " +
                Marshal.GetLastWin32Error() + ").");
        }

        return handle;
    }

    private void ApplyFonts()
    {
        _fonts.Create(_dpi);
        foreach (var control in new[]
        {
            _tabStrip, _addressCombo, _noticeText, _startSubtitleText,
            _newTabButton, _closeActiveTabButton, _backButton, _forwardButton, _reloadButton,
            _bookmarkButton, _menuButton,
            _noticeActionButton, _noticeDismissButton, _titleMinimize, _titleMaximize, _titleClose,
        })
        {
            if (control != IntPtr.Zero) NativeMethods.SetWindowFont(control, _fonts.Body, false);
        }

        if (_startTitleText != IntPtr.Zero) NativeMethods.SetWindowFont(_startTitleText, _fonts.Display, false);

        _buttons.Refresh();
        _fields.Refresh(Scale(ControlSize));
    }

    private void Label(IntPtr button, string labelKey, string tooltipKey)
    {
        if (button == IntPtr.Zero) return;
        NativeMethods.SetWindowText(button, _localization.Get(labelKey));
        _buttons.SetTooltip(button, _localization.Get(tooltipKey));
    }

    private void UpdateMaximizeButton()
    {
        if (_titleMaximize == IntPtr.Zero) return;
        var maximized = NativeMethods.IsZoomed(_hwnd);
        Label(_titleMaximize, maximized ? "window.restore" : "window.maximize", maximized ? "window.restore" : "window.maximize");
        _buttons.SetGlyph(_titleMaximize, maximized ? GlyphRestore : GlyphMaximize);
    }

    private static void ShowControls(bool visible, params IntPtr[] controls)
    {
        foreach (var control in controls)
        {
            NativeMethods.ShowWindow(control, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        }
    }

    private void RenderStartPage()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowText(_startTitleText, _startTitle);
        NativeMethods.SetWindowText(_startSubtitleText, _startSubtitle);
        Layout();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    // ---------- Layout ----------

    private int Scale(int value) => NativeMethods.Scale(value, _dpi);

    private void Layout()
    {
        if (_hwnd == IntPtr.Zero || _tabStrip == IntPtr.Zero) return;
        NativeMethods.GetClientRect(_hwnd, out var client);
        var width = Math.Max(0, client.Width);
        LayoutCaptionRow(width);
        LayoutToolbar(width);
        LayoutNotice(width);

        var bounds = GetContentBounds();
        ContentBoundsChanged?.Invoke(this, bounds);
        LayoutStartPage(bounds);
    }

    private void LayoutCaptionRow(int width)
    {
        var captionButtonWidth = Scale(CaptionButtonWidth);
        var captionButtonHeight = Scale(CaptionButtonHeight);
        NativeMethods.MoveWindow(_titleMinimize, width - captionButtonWidth * 3, 0, captionButtonWidth, captionButtonHeight, true);
        NativeMethods.MoveWindow(_titleMaximize, width - captionButtonWidth * 2, 0, captionButtonWidth, captionButtonHeight, true);
        NativeMethods.MoveWindow(_titleClose, width - captionButtonWidth, 0, captionButtonWidth, captionButtonHeight, true);

        var stripRight = SizeTabStrip(width, _tabMirror.Count);
        var tabTop = Scale(TabTop);
        var tabHeight = Scale(CaptionHeight - TabTop);
        var actionSize = Scale(28);
        var actionTop = tabTop + (tabHeight - actionSize) / 2;
        NativeMethods.MoveWindow(_newTabButton, stripRight + Scale(4), actionTop, actionSize, actionSize, true);
        PositionTabClose();
    }

    /// <summary>
    /// Sizes the tabs and the strip for a tab count and returns the strip's right edge. Tabs share the
    /// space left of the new-tab button and a minimum drag area, and always fit: a tab control whose
    /// items overflow scrolls them and adds its own system scroll arrows, which the custom painting
    /// cannot follow. <see cref="UpdateTabs"/> calls this before inserting items, so the strip never
    /// overflows, not even for the moment between inserting a tab and laying out.
    /// </summary>
    private int SizeTabStrip(int width, int tabCount)
    {
        var tabTop = Scale(TabTop);
        var tabHeight = Scale(CaptionHeight - TabTop);
        var stripLeft = Scale(8);
        var actionsWidth = Scale(4) + Scale(28) + Scale(4);
        var available = Math.Max(Scale(MinimumTabWidth),
            width - stripLeft - actionsWidth - Scale(CaptionButtonWidth) * 3 - Scale(48));
        var count = Math.Max(1, tabCount);
        // The control indents the first item and widens the selected one, so keep some slack.
        var slack = Scale(8);
        var tabWidth = Math.Max(1, Math.Min(Scale(MaximumTabWidth), (available - slack) / count));
        NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_SETITEMSIZE, IntPtr.Zero,
            new IntPtr(NativeMethods.MakeLong(tabWidth, tabHeight)));
        var stripWidth = Math.Min(available, tabWidth * count + slack);
        NativeMethods.MoveWindow(_tabStrip, stripLeft, tabTop, stripWidth, tabHeight, true);
        HideTabScroller();
        _laidOutTabCount = tabCount;
        return stripLeft + stripWidth;
    }

    private void LayoutToolbar(int width)
    {
        var size = Scale(ControlSize);
        var gap = Scale(4);
        var top = Scale(CaptionHeight) + (Scale(ToolbarHeight) - size) / 2;
        var x = Scale(8);
        foreach (var button in new[] { _backButton, _forwardButton, _reloadButton })
        {
            NativeMethods.MoveWindow(button, x, top, size, size, true);
            x += size + gap;
        }

        var right = width - Scale(8);
        foreach (var button in new[] { _menuButton, _bookmarkButton })
        {
            right -= size;
            NativeMethods.MoveWindow(button, right, top, size, size, true);
            right -= gap;
        }

        // A drop-down combo box treats its window height as the open list height; the field renderer
        // sizes the closed field to the toolbar control size.
        var addressLeft = x + Scale(4);
        var addressWidth = Math.Max(Scale(120), right - Scale(4) - addressLeft);
        NativeMethods.MoveWindow(_addressCombo, addressLeft, top, addressWidth, Scale(320), true);
    }

    private void LayoutNotice(int width)
    {
        if (!_noticeVisible) return;
        var top = Scale(CaptionHeight + ToolbarHeight) + 1;
        var height = Scale(NoticeHeight);
        var buttonHeight = Scale(28);
        var buttonTop = top + (height - buttonHeight) / 2;
        var gap = Scale(8);
        var right = width - Scale(12);
        foreach (var button in new[] { _noticeDismissButton, _noticeActionButton })
        {
            if (!NativeMethods.IsWindowVisible(button)) continue;
            var buttonWidth = TextButtonWidth(button);
            right -= buttonWidth;
            NativeMethods.MoveWindow(button, right, buttonTop, buttonWidth, buttonHeight, true);
            right -= gap;
        }

        var textHeight = NativeFonts.Measure(_hwnd, _fonts.Body, "Ag").y;
        var left = Scale(16);
        NativeMethods.MoveWindow(_noticeText, left, top + (height - textHeight) / 2, Math.Max(Scale(80), right - left), textHeight, true);
    }

    private void LayoutStartPage(ContentBounds bounds)
    {
        var width = Math.Min(bounds.Width - Scale(64), Scale(560));
        var left = (bounds.Width - width) / 2;
        var titleHeight = NativeFonts.Measure(_hwnd, _fonts.Display, "Ag").y + Scale(4);
        var titleTop = bounds.Y + Math.Max(Scale(32), bounds.Height * 3 / 10);
        NativeMethods.MoveWindow(_startTitleText, left, titleTop, width, titleHeight, true);
        NativeMethods.MoveWindow(_startSubtitleText, left, titleTop + titleHeight + Scale(12), width, Scale(96), true);
        ShowControls(_startPageVisible, _startTitleText, _startSubtitleText);
    }

    private int TextButtonWidth(IntPtr button) =>
        Math.Max(Scale(72), NativeFonts.Measure(_hwnd, _fonts.Body, NativeMethods.GetWindowText(button)).x + Scale(24));

    private int ContentTop()
    {
        var top = Scale(CaptionHeight + ToolbarHeight) + 1;
        if (_noticeVisible) top += Scale(NoticeHeight);
        return top;
    }

    // ---------- Tab strip ----------

    private void InsertTab(int index, string text)
    {
        var item = new NativeMethods.TCITEM
        {
            mask = NativeMethods.TCIF_TEXT,
            pszText = text,
        };
        NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_INSERTITEM, new IntPtr(index), ref item);
    }

    private void SetTabText(int index, string text)
    {
        var item = new NativeMethods.TCITEM
        {
            mask = NativeMethods.TCIF_TEXT,
            pszText = text,
        };
        NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_SETITEM, new IntPtr(index), ref item);
    }

    private NativeMethods.RECT TabRect(int index)
    {
        var rect = new NativeMethods.RECT();
        NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_GETITEMRECT, new IntPtr(index), ref rect);
        return rect;
    }

    /// <summary>
    /// Removes the tab control's overflow arrows. They are a system up-down control that ignores the
    /// theme, and they are never needed because tabs are sized to fit; any scroll offset left from
    /// an earlier overflow is reset first so the first tab is at the left edge again.
    /// </summary>
    private void HideTabScroller()
    {
        var scroller = NativeMethods.FindWindowEx(_tabStrip, IntPtr.Zero, "msctls_updown32", null);
        if (scroller == IntPtr.Zero) return;
        NativeMethods.SendMessage(_tabStrip, NativeMethods.WM_HSCROLL,
            new IntPtr(NativeMethods.MakeLong(NativeMethods.SB_THUMBPOSITION, 0)), scroller);
        NativeMethods.ShowWindow(scroller, NativeMethods.SW_HIDE);
        NativeMethods.InvalidateRect(_tabStrip, IntPtr.Zero, false);
    }

    /// <summary>Places the close button inside the right edge of the active tab.</summary>
    private void PositionTabClose()
    {
        var index = SelectedTabIndex;
        NativeMethods.GetWindowRect(_tabStrip, out var strip);
        if (index < 0 || index >= _tabMirror.Count)
        {
            ShowControls(false, _closeActiveTabButton);
            return;
        }

        var tab = TabRect(index);
        var size = Scale(20);
        var origin = new NativeMethods.POINT { x = strip.left, y = strip.top };
        NativeMethods.ScreenToClient(_hwnd, ref origin);
        var visible = tab.right <= strip.Width && tab.Width >= Scale(MinimumTabWidth);
        NativeMethods.MoveWindow(_closeActiveTabButton,
            origin.x + tab.right - size - Scale(8), origin.y + (strip.Height - size) / 2, size, size, true);
        ShowControls(visible, _closeActiveTabButton);
    }

    private void PaintTabStrip(IntPtr hWnd)
    {
        var paint = new NativeMethods.PAINTSTRUCT();
        var target = NativeMethods.BeginPaint(hWnd, ref paint);
        NativeMethods.GetClientRect(hWnd, out var client);
        var dc = NativeMethods.CreateCompatibleDC(target);
        var bitmap = NativeMethods.CreateCompatibleBitmap(target, Math.Max(1, client.Width), Math.Max(1, client.Height));
        var previousBitmap = NativeMethods.SelectObject(dc, bitmap);
        try
        {
            var palette = _theme.Palette;
            using var scope = NativePaintScope.Begin(dc, "tab strip");
            NativePaint.Fill(dc, client, palette.Frame, _theme);
            var selected = SelectedTabIndex;
            var radius = Scale(8);
            var padding = Scale(12);
            for (var index = 0; index < _tabMirror.Count; index++)
            {
                var item = TabRect(index);
                var rect = NativePaint.Rect(item.left, 0, item.Width, client.Height);
                var active = index == selected;
                var hot = index == _hotTab && !active;
                if (active)
                {
                    // The active tab merges into the toolbar below it.
                    var body = rect;
                    body.bottom += radius;
                    NativePaint.FillRounded(dc, body, radius, palette.Surface, _theme);
                }
                else if (hot)
                {
                    var body = rect;
                    body.left += Scale(2);
                    body.right -= Scale(2);
                    body.top += Scale(3);
                    body.bottom -= Scale(4);
                    NativePaint.FillRounded(dc, body, Scale(6), palette.Hover, _theme);
                }

                var neighborEmphasized = index + 1 == selected || index + 1 == _hotTab;
                if (!active && !hot && !neighborEmphasized && index + 1 < _tabMirror.Count)
                {
                    var divider = Scale(16);
                    NativePaint.Fill(dc, NativePaint.Rect(rect.right - 1, (client.Height - divider) / 2, 1, divider), palette.Border, _theme);
                }

                var textRect = rect;
                textRect.left += padding;
                textRect.right -= active ? Scale(32) : padding;
                if (_tabMirror[index].Private)
                {
                    var glyphRect = textRect;
                    glyphRect.right = glyphRect.left + Scale(18);
                    NativePaint.Text(dc, GlyphPrivate, glyphRect, _fonts.SmallIcon, palette.Accent,
                        NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
                    textRect.left += Scale(18);
                }

                NativePaint.Text(dc, _tabMirror[index].Text, textRect, _fonts.Body,
                    active ? palette.Text : palette.SecondaryText,
                    NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE | NativeMethods.DT_END_ELLIPSIS);
            }

            NativeMethods.BitBlt(target, 0, 0, client.Width, client.Height, dc, 0, 0, NativeMethods.SRCCOPY);
        }
        finally
        {
            NativeMethods.SelectObject(dc, previousBitmap);
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(dc);
            NativeMethods.EndPaint(hWnd, ref paint);
        }
    }

    private string TabText(BrowserTab tab, bool loading = false)
    {
        var title = DisplayTitle(tab);
        if (loading && title.Length > 0 && !title.EndsWith("...", StringComparison.Ordinal))
            title += "...";
        return Truncate(title);
    }

    private string DisplayTitle(BrowserTab tab)
    {
        if (string.IsNullOrWhiteSpace(tab.Url))
        {
            return _localization.Get(tab.SessionKind == BrowserSessionKind.Private ? "tab.newPrivate" : "tab.new");
        }

        if (!string.IsNullOrWhiteSpace(tab.Title) && !string.Equals(tab.Title, "New Tab", StringComparison.Ordinal))
        {
            return Decorate(tab, tab.Title);
        }

        return Decorate(tab, FallbackTitle(tab.Url));
    }

    private string Decorate(BrowserTab tab, string title) =>
        tab.SessionKind == BrowserSessionKind.Private
            ? _localization.Format("tab.privateLabel", "title", title)
            : title;

    /// <summary>A readable label for a URL whose document title is not known yet.</summary>
    private string FallbackTitle(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (NavigationInputParser.IsInternalPage(uri))
            {
                if (uri.Host.Equals("settings", StringComparison.OrdinalIgnoreCase)) return _localization.Get("settings.title");
                if (uri.Host.Equals("history", StringComparison.OrdinalIgnoreCase)) return _localization.Get("history.title");
                return _localization.Get("bookmarks.title");
            }

            if (!string.IsNullOrEmpty(uri.Host)) return uri.Host;
            if (uri.IsFile)
            {
                var fileName = Path.GetFileNameWithoutExtension(uri.LocalPath);
                if (!string.IsNullOrEmpty(fileName)) return fileName;
            }
        }

        return url;
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaximumTabTextLength) return text;
        return text.Substring(0, MaximumTabTextLength - 1) + "…";
    }

    // ---------- Subclasses ----------

    private IntPtr OnTabStripMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        switch (msg)
        {
            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);

            case NativeMethods.WM_PAINT:
                PaintTabStrip(hWnd);
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEMOVE:
                var hot = HitTestTab(lParam);
                if (hot != _hotTab)
                {
                    _hotTab = hot;
                    var track = new NativeMethods.TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                        dwFlags = NativeMethods.TME_LEAVE,
                        hwndTrack = hWnd,
                    };
                    NativeMethods.TrackMouseEvent(ref track);
                    NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                }

                break;

            case NativeMethods.WM_MOUSELEAVE:
                _hotTab = -1;
                NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                break;

            case NativeMethods.WM_MBUTTONDOWN:
                // One click closes one tab: the button-up message is swallowed, not acted on.
                var index = HitTestTab(lParam);
                if (index >= 0)
                {
                    CloseTabRequested?.Invoke(this, _tabMirror[index].Id);
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WM_MBUTTONUP:
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEWHEEL:
                var wheelDelta = unchecked((short)NativeMethods.GetHiWord(wParam));
                ScrollTabs(wheelDelta > 0 ? -1 : 1);
                return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private int HitTestTab(IntPtr lParam)
    {
        var info = new NativeMethods.TCHITTESTINFO
        {
            pt = new NativeMethods.POINT
            {
                x = unchecked((short)NativeMethods.GetLoWord(lParam)),
                y = unchecked((short)NativeMethods.GetHiWord(lParam)),
            },
        };
        var index = (int)NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_HITTEST, IntPtr.Zero, ref info);
        return index >= 0 && index < _tabMirror.Count ? index : -1;
    }

    private void ScrollTabs(int step)
    {
        var current = SelectedTabIndex;
        var next = current + step;
        if (next < 0 || next >= _tabMirror.Count) return;
        NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_SETCURSEL, new IntPtr(next), IntPtr.Zero);
        // Programmatic TCM_SETCURSEL does not emit TCN_SELCHANGE. Keep Core's active tab
        // synchronized when the user changes tabs with the wheel.
        ActivateTabRequested?.Invoke(this, _tabMirror[next].Id);
    }

    private IntPtr OnAddressEditMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        switch (msg)
        {
            case NativeMethods.WM_KEYDOWN:
                if (NativeMethods.GetLoWord(wParam) == NativeMethods.VK_RETURN)
                {
                    SubmitAddress();
                    return IntPtr.Zero;
                }

                if (NativeMethods.GetLoWord(wParam) == NativeMethods.VK_ESCAPE)
                {
                    RevertAddress();
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WM_CHAR:
                if (NativeMethods.GetLoWord(wParam) == NativeMethods.VK_RETURN) return IntPtr.Zero;
                break;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void SubmitAddress()
    {
        var text = NativeMethods.GetWindowText(_addressCombo);
        if (!string.IsNullOrWhiteSpace(text)) NavigateRequested?.Invoke(this, text);
    }

    private void RevertAddress()
    {
        NativeMethods.SetWindowText(_addressCombo, _currentUrl);
        NativeMethods.SendMessage(
            _addressEdit != IntPtr.Zero ? _addressEdit : _addressCombo,
            NativeMethods.EM_SETSEL,
            IntPtr.Zero,
            new IntPtr(-1));
    }

    // ---------- Commands ----------

    private void OnCommand(IntPtr wParam, IntPtr lParam)
    {
        var controlId = NativeMethods.GetLoWord(wParam);
        var notification = NativeMethods.GetHiWord(wParam);

        // Without a control handle, WM_COMMAND is a menu command (notification 0), as Win32 defines it.
        if (lParam == IntPtr.Zero)
        {
            if (notification == 0) InvokeCommand(controlId);
            return;
        }

        var control = lParam;

        if (controlId == IdAddress)
        {
            if (notification == NativeMethods.CBN_SELCHANGE)
            {
                var selected = (int)NativeMethods.SendMessage(control, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
                if (selected >= 0)
                {
                    var text = NativeMethods.GetComboItemText(control, selected);
                    if (!string.IsNullOrWhiteSpace(text)) NavigateRequested?.Invoke(this, text);
                }
            }
            else if (notification == NativeMethods.CBN_DROPDOWN)
            {
                PopulateAddressSuggestions();
            }

            return;
        }

        if (notification == NativeMethods.BN_CLICKED) InvokeCommand(controlId);
    }

    /// <summary>
    /// Runs a browser command by id, whether it came from a button, the toolbar menu, or the Debug
    /// smoke harness. Commands that moved into the menu keep their ids and their behavior.
    /// </summary>
    public void InvokeCommand(int commandId)
    {
        switch (commandId)
        {
            case IdMenu:
                ShowMenu();
                break;
            case IdBack:
                BackRequested?.Invoke(this, EventArgs.Empty);
                break;
            case IdForward:
                ForwardRequested?.Invoke(this, EventArgs.Empty);
                break;
            case IdReload:
                ReloadRequested?.Invoke(this, EventArgs.Empty);
                break;
            case IdNewTab:
                NewTabRequested?.Invoke(this, EventArgs.Empty);
                break;
            case IdNewPrivateTab:
                NewPrivateTabRequested?.Invoke(this, EventArgs.Empty);
                break;
            case IdCloseActiveTab:
                var selected = SelectedTabIndex;
                if (selected >= 0 && selected < _tabMirror.Count)
                    CloseTabRequested?.Invoke(this, _tabMirror[selected].Id);
                break;
            case IdBookmark:
                ToggleBookmarkRequested?.Invoke(this, EventArgs.Empty);
                break;
            case IdHistory:
                OpenPanelRequested?.Invoke(this, "history");
                break;
            case IdBookmarks:
                OpenPanelRequested?.Invoke(this, "bookmarks");
                break;
            case IdSettings:
                OpenPanelRequested?.Invoke(this, "settings");
                break;
            case IdNoticeAction:
                NoticeReloadRequested?.Invoke(this, EventArgs.Empty);
                break;
            case IdNoticeDismiss:
                NoticeDismissRequested?.Invoke(this, EventArgs.Empty);
                break;
            case IdTitleMinimize:
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_MINIMIZE);
                break;
            case IdTitleMaximize:
                NativeMethods.ShowWindow(_hwnd, NativeMethods.IsZoomed(_hwnd) ? NativeMethods.SW_RESTORE : NativeMethods.SW_MAXIMIZE);
                break;
            case IdTitleClose:
                OnClose();
                break;
        }
    }

    /// <summary>Opens the toolbar menu under its button, right-aligned with it.</summary>
    private void ShowMenu()
    {
        if (_menu.IsOpen) return;
        var commands = new[] { IdNewTab, IdNewPrivateTab, IdHistory, IdBookmarks, IdSettings };
        var items = new[]
        {
            new FlatPopupItem(_localization.Get("tab.newAction"), "Ctrl+T"),
            new FlatPopupItem(_localization.Get("tab.newPrivateAction"), "Ctrl+Shift+N"),
            new FlatPopupItem(_localization.Get("toolbar.history"), "Ctrl+H", separatorBefore: true),
            new FlatPopupItem(_localization.Get("toolbar.bookmarks")),
            new FlatPopupItem(_localization.Get("toolbar.settings"), separatorBefore: true),
        };
        NativeMethods.GetWindowRect(_menuButton, out var anchor);
        _menu.Show(_hwnd, _menuButton, anchor, items,
            chosen: index => InvokeCommand(commands[index]),
            alignRight: true,
            minimumWidth: Scale(240));
    }

    private void PopulateAddressSuggestions()
    {
        var suggestions = AddressSuggestionsProvider?.Invoke();
        if (suggestions is null) return;
        NativeMethods.SendMessage(_addressCombo, NativeMethods.CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        foreach (var suggestion in suggestions)
        {
            NativeMethods.SendMessage(_addressCombo, NativeMethods.CB_ADDSTRING, IntPtr.Zero, suggestion);
        }

        // Resetting the list clears the edit text; keep what the user sees.
        NativeMethods.SetWindowText(_addressCombo, _currentUrl);
    }

    private void OnNotify(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;
        var header = Marshal.PtrToStructure<NativeMethods.NMHDR>(lParam);
        if (header.hwndFrom != _tabStrip) return;

        if (header.code == NativeMethods.TCN_SELCHANGE)
        {
            var index = SelectedTabIndex;
            PositionTabClose();
            if (index >= 0 && index < _tabMirror.Count)
            {
                ActivateTabRequested?.Invoke(this, _tabMirror[index].Id);
            }
        }
    }

    // ---------- Keyboard ----------

    private static bool IsKeyDown(uint message) =>
        message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN;

    private bool TryHandleKey(uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (!IsKeyDown(message)) return false;
        if (_hwnd == IntPtr.Zero || NativeMethods.GetActiveWindow() != _hwnd) return false;

        var virtualKey = NativeMethods.GetLoWord(wParam);
        var control = (NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        var shift = (NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        var alt = (NativeMethods.GetKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;

        // Alt+F opens the toolbar menu, as in other Windows browsers.
        if (alt && !control && virtualKey == 0x46)
        {
            ShowMenu();
            return true;
        }

        var args = new BrowserShortcutEventArgs(virtualKey, control, alt, shift);
        ShortcutPressed?.Invoke(this, args);
        return args.Handled;
    }

    /// <summary>Starts or stops the periodic browser timers.</summary>
    public void SetTimerEnabled(int timerId, bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (enabled)
        {
            var interval = timerId == SleepTimerId ? 60000u : 5000u;
            NativeMethods.SetTimer(_hwnd, new IntPtr(timerId), interval, IntPtr.Zero);
        }
        else
        {
            NativeMethods.KillTimer(_hwnd, new IntPtr(timerId));
        }
    }
}

/// <summary>The content rectangle in the window's client coordinates.</summary>
internal readonly struct ContentBounds
{
    public ContentBounds(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }
}
