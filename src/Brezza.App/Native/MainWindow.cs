using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Brezza.App.Native;
using Brezza.Core.Abstractions;
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
internal sealed partial class MainWindow : IDisposable
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
    public const int IdCookies = 1019;
    public const int IdNotice = 1020;
    public const int IdNoticeAction = 1021;
    public const int IdNoticeDismiss = 1022;
    public const int IdStartTitle = 1023;
    public const int IdStartSubtitle = 1024;
    public const int IdBrowsingData = 1025;
    public const int IdDownloads = 1026;
    public const int IdTitleMinimize = 1031;
    public const int IdTitleMaximize = 1032;
    public const int IdTitleClose = 1033;
    public const int IdStartRecentHeader = 1040;
    public const int IdStartBookmarksHeader = 1041;

    // The new-tab page's shortcut rows: the recent column first, then the bookmarks column.
    public const int IdStartEntryFirst = 1100;
    public const int StartEntrySlots = 6;
    private const int SleepTimerId = 1;
    private const int ShutdownGuardTimerId = 3;

    private const int MaximumTabTextLength = 80;
    private const int MinimumWindowWidth = 640;
    private const int MinimumWindowHeight = 420;

    // The slot a tab's favicon occupies, and the gap between it and the label.
    private const int TabIconSize = 16;

    // Unscaled metrics at 96 DPI.
    private const int CaptionHeight = 40;
    private const int TabTop = 6;
    private const int ToolbarHeight = 44;
    private const int NoticeHeight = 40;
    private const int CaptionButtonWidth = 46;
    private const int MaximumTabWidth = 240;

    // The active tab's close button: its widest size, the gap it keeps from its tab's edges, and
    // how far it sits in from the tab's right edge. The minimum tab width is derived from these.
    private const int TabCloseSize = 20;
    private const int TabCloseMargin = 4;
    private const int TabCloseInset = 8;

    // The slot a private tab's leading marker is drawn in.
    private const int TabPrivateMarkerWidth = 18;

    // Icon font glyphs (Segoe Fluent Icons, with Segoe MDL2 Assets code points).
    private const string GlyphBack = "\uE72B";
    private const string GlyphForward = "\uE72A";
    private const string GlyphReload = "\uE72C";
    private const string GlyphStop = "\uE71A";
    // A speaker: the tab's page is playing sound.
    private const string GlyphAudio = "\uE767";
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
    // A globe: the icon a tab falls back to when its page has no favicon, since a page without one
    // is still a page on the web.
    private const string GlyphGlobe = "\uE774";

    private readonly NativeMethods.WndProc _wndProc;
    private readonly NativeMethods.SubClassProc _tabStripSubClass;
    private readonly NativeMethods.SubClassProc _addressEditSubClass;
    private readonly Localization _localization;
    private readonly NativeThemeSource _themeSource;
    private readonly NativeThemeResources _theme;
    private readonly NativeFonts _fonts = new();
    private readonly FlatButtonSet _buttons;
    private readonly FlatFieldSet _fields;
    private readonly FlatPopup _menu;

    // The tab favicons to paint, looked up per tab id. The window renders the images it is given
    // and never fetches or caches them itself.
    private readonly Func<string, System.Drawing.Bitmap?>? _favicons;

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
    private IntPtr _startRecentHeader;
    private IntPtr _startBookmarksHeader;
    private readonly IntPtr[] _startEntryButtons = new IntPtr[StartEntrySlots * 2];
    private readonly string[] _startEntryUrls = new string[StartEntrySlots * 2];
    private StartPageEntries _startEntries = StartPageEntries.Empty;

    private readonly List<(string Id, string Text, bool Private, bool Audio)> _tabMirror = new();
    private int _hotTab = -1;

    // Whether the pointer is over the hot tab's close glyph, which an inactive tab shows on hover.
    private bool _hotTabClose;

    // Whether the active page is loading; the reload button is a stop button while it is.
    private bool _loading;
    private int _laidOutTabCount = -1;

    // Past the point where every tab still fits at a readable width, the strip is wider than the
    // space it shows and is moved sideways to scroll. Moving the strip window instead of letting
    // the tab control overflow keeps its system scroll arrows from ever appearing.
    private int _tabStripWidth;
    private int _tabVisibleWidth;
    private int _tabScrollOffset;
    private int _tabMaxScroll;
    private bool _tabStripScrollable;
    private string _currentUrl = string.Empty;
    private string? _activeTabId;
    private bool _addressEdited;
    private bool _updatingAddressText;
    private bool _noticeVisible;
    private bool _bookmarked;
    private bool _startPageVisible;
    private string _startTitle = string.Empty;
    private string _startSubtitle = string.Empty;

    public MainWindow(Localization localization, NativeThemeSource themeSource,
        Func<string, System.Drawing.Bitmap?>? favicons = null)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _themeSource = themeSource ?? throw new ArgumentNullException(nameof(themeSource));
        _favicons = favicons;
        _theme = new NativeThemeResources(_themeSource);
        _themeSource.Changed += OnThemeChanged;
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
    public event EventHandler? SessionEndingRequested;
    public event EventHandler<string>? NavigateRequested;
    public event EventHandler<string>? ActivateTabRequested;
    public event EventHandler<string>? CloseTabRequested;
    public event EventHandler? NewTabRequested;
    public event EventHandler? NewPrivateTabRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? ForwardRequested;
    public event EventHandler? ReloadRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<TabCommand>? TabCommandRequested;

    /// <summary>A shortcut on the new-tab page was chosen; the argument is its URL.</summary>
    public event EventHandler<string>? StartEntryRequested;
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

        // CreateWindowEx takes physical pixels. Resize to the intended logical 1120x760 before the
        // first Show, so 125%/150% displays do not open a visibly smaller window.
        if (_dpi != NativeMethods.USER_DEFAULT_SCREEN_DPI)
        {
            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                0,
                0,
                Scale(1120),
                Scale(760),
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        ApplyFonts();
        RepaintTheme();

        // WM_CREATE runs before CreateWindowEx returns the top-level HWND, so the first
        // language pass is intentionally guarded. Apply it again once all handles exist.
        ApplyLanguage();
        DialogKeyboard.Register(_hwnd, tabOnly: true);
    }

    public void Show()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        NativeMethods.UpdateWindow(_hwnd);
    }

    public int RunMessageLoop()
    {
        while (true)
        {
            var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result == 0) return 0;
            if (result == -1)
            {
                Trace.TraceError("The browser message loop could not read a message (error {0}).", Marshal.GetLastWin32Error());
                return 1;
            }

            if (IsKeyDown(message.message) && FlatPopup.Current is { } popup &&
                popup.TryHandleKey(NativeMethods.GetLoWord(message.wParam))) continue;
            if (TryHandleKey(message.message, message.wParam, message.lParam)) continue;
            if (DialogKeyboard.TryTranslate(ref message)) continue;
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }
    }

    /// <summary>Destroys the window after the shell finished its asynchronous shutdown.</summary>
    public void CompleteShutdown()
    {
        if (_hwnd == IntPtr.Zero) return;
        _shutdownCompleted = true;
        NativeMethods.KillTimer(_hwnd, new IntPtr(ShutdownGuardTimerId));
        var window = _hwnd;
        _hwnd = IntPtr.Zero;
        NativeMethods.DestroyWindow(window);
    }

    public void Dispose()
    {
        DialogKeyboard.Unregister(_hwnd);

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
        _themeSource.Changed -= OnThemeChanged;
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
        ApplyReloadButton();
        Label(_menuButton, "toolbar.menu", "toolbar.menuShortcut");
        Label(_titleMinimize, "window.minimize", "window.minimize");
        Label(_titleClose, "window.close", "window.close");
        UpdateMaximizeButton();
        SetBookmarked(_bookmarked);
        NativeMethods.SetWindowText(_noticeActionButton, _localization.Get("notice.reload"));
        NativeMethods.SetWindowText(_noticeDismissButton, _localization.Get("notice.dismiss"));
        NativeMethods.SetWindowText(_startTitleText, _startTitle);
        NativeMethods.SetWindowText(_startSubtitleText, _startSubtitle);
        NativeMethods.SetWindowText(_startRecentHeader, _localization.Get("start.recent"));
        NativeMethods.SetWindowText(_startBookmarksHeader, _localization.Get("start.bookmarks"));

        // The address field has no placeholder property, so the cue banner carries the label.
        NativeMethods.SendMessage(
            _addressEdit != IntPtr.Zero ? _addressEdit : _addressCombo,
            0x1501, // EM_SETCUEBANNER
            IntPtr.Zero,
            _localization.Get("address.placeholder"));

        Layout();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => RepaintTheme();

    /// <summary>
    /// Repaints every native surface for the palette the shared source resolved. The window owns no
    /// palette of its own: it renders state it is given, so a theme change is one synchronous pass.
    /// </summary>
    private void RepaintTheme()
    {
        // Brushes of the previous palette must be gone before anything repaints: a control that
        // erases itself with a stale brush, and paints with a fresh one, is what makes a theme
        // switch arrive in two steps.
        _theme.ReleaseBrushes();
        if (_hwnd == IntPtr.Zero) return;

        _theme.ApplyToWindowFrame(_hwnd);
        var tabTooltips = NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_GETTOOLTIPS, IntPtr.Zero, IntPtr.Zero);
        _theme.ApplyToControl(tabTooltips);
        _buttons.Refresh();
        _fields.Refresh(Scale(NativeMetrics.ControlHeight));

        // The frame change re-applies the window corner and non-client metrics.
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
        Layout();

        // Dependent surfaces (content background, open panel) take the palette before anything
        // repaints, then the whole window repaints at once. Invalidating controls one by one lets
        // the message loop repaint them on separate turns, so a switch arrives in visible steps.
        ThemeChanged?.Invoke(this, EventArgs.Empty);
        NativeMethods.RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, NativeMethods.RDW_REPAINT_ALL_NOW);
    }

    public void UpdateTabs(IReadOnlyList<BrowserTab> tabs, string? activeTabId, bool activeLoading = false)
    {
        if (_hwnd == IntPtr.Zero || _tabStrip == IntPtr.Zero) return;

        // A switch to another tab discards an unsubmitted address draft: the field must show the
        // new tab's address, not text the user was typing for the previous one.
        if (!string.Equals(_activeTabId, activeTabId, StringComparison.Ordinal))
        {
            _activeTabId = activeTabId;
            _addressEdited = false;
        }

        var desired = tabs
            .Select(tab => (tab.Id, Text: TabText(tab, tab.Id == activeTabId && activeLoading),
                Private: tab.SessionKind == BrowserSessionKind.Private, Audio: tab.IsPlayingAudio))
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

            var activeIndex = -1;
            for (var index = 0; index < tabs.Count; index++)
            {
                if (string.Equals(tabs[index].Id, activeTabId, StringComparison.Ordinal))
                {
                    activeIndex = index;
                    break;
                }
            }

            // A missing active id means the shell's state is changing; do not silently select the
            // first tab and make the native view disagree with Core.
            if (activeIndex >= 0)
            {
                // Scroll to the new active tab before the selection moves, so the control's own
                // repaint of the new selection and the strip's repaint at the new offset are one paint.
                ScrollTabIntoView(activeIndex);
                NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_SETCURSEL, new IntPtr(activeIndex), IntPtr.Zero);
            }
        }
        finally
        {
            NativeMethods.SendMessage(_tabStrip, NativeMethods.WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
        }

        // The strip was sized above; on a count change the new-tab button follows it. Otherwise only
        // the close button follows the selection, which keeps title updates cheap.
        if (countChanged) Layout();
        else PositionTabClose();
        // The strip repaints on every update: the audio marker and the hover close glyph are painted,
        // not item text, so an unchanged item list can still look different.
        NativeMethods.InvalidateRect(_tabStrip, IntPtr.Zero, false);
    }

    /// <summary>
    /// Updates the navigation buttons. While the page loads, the reload button stops the load
    /// instead, as in other browsers, so one place in the toolbar answers "is this page done".
    /// </summary>
    public void UpdateNavigationState(bool canGoBack, bool canGoForward, bool loading = false)
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.EnableWindow(_backButton, canGoBack);
        NativeMethods.EnableWindow(_forwardButton, canGoForward);
        NativeMethods.EnableWindow(_reloadButton, loading || _currentUrl.Length > 0);
        if (_loading == loading) return;
        _loading = loading;
        ApplyReloadButton();
    }

    private void ApplyReloadButton()
    {
        if (_reloadButton == IntPtr.Zero) return;
        if (_loading) Label(_reloadButton, "toolbar.stop", "toolbar.stopShortcut");
        else Label(_reloadButton, "toolbar.reload", "toolbar.reloadShortcut");
        _buttons.SetGlyph(_reloadButton, _loading ? GlyphStop : GlyphReload);
    }

    /// <summary>Whether keyboard focus is in the address field.</summary>
    public bool AddressHasFocus
    {
        get
        {
            var focus = NativeMethods.GetFocus();
            return focus != IntPtr.Zero && (focus == _addressCombo || NativeMethods.IsChild(_addressCombo, focus));
        }
    }

    /// <summary>
    /// Repaints the tab strip. A favicon that finishes loading after its tab's last render needs
    /// its own repaint; the strip is cheap to paint and this keeps the icon from waiting for the
    /// next unrelated state change.
    /// </summary>
    public void InvalidateTabStrip()
    {
        if (_tabStrip == IntPtr.Zero) return;
        NativeMethods.InvalidateRect(_tabStrip, IntPtr.Zero, false);
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
        if (_hwnd == IntPtr.Zero || _addressEdited) return;
        SetAddressTextCore(_currentUrl);
    }

    /// <summary>
    /// Writes the address field without treating the change as user input. Programmatic
    /// <c>SetWindowText</c> also raises the edit notifications that mark a draft dirty.
    /// </summary>
    private void SetAddressTextCore(string text)
    {
        _updatingAddressText = true;
        try
        {
            NativeMethods.SetWindowText(_addressCombo, text);
        }
        finally
        {
            _updatingAddressText = false;
        }
    }

    public void SetWindowTitle(string title)
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowText(_hwnd, title);
    }

    /// <summary>Brings the browser window to the foreground, for example when a panel closes.</summary>
    public void Activate()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    public void FocusAddress()
    {
        if (_hwnd == IntPtr.Zero || _addressCombo == IntPtr.Zero) return;
        var target = _addressEdit != IntPtr.Zero ? _addressEdit : _addressCombo;
        NativeMethods.SetFocus(target);
        NativeMethods.SendMessage(target, NativeMethods.EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
    }

    public void SetNotice(string? key, string? detail, NoticeAction action = NoticeAction.None)
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

        // The shell decides whether a notice is recoverable; the button only says how.
        if (action != NoticeAction.None)
        {
            NativeMethods.SetWindowText(_noticeActionButton,
                _localization.Get(action == NoticeAction.Retry ? "notice.retry" : "notice.reload"));
        }

        ShowControls(action != NoticeAction.None, _noticeActionButton);
        ShowControls(true, _noticeText, _noticeDismissButton);
        _noticeVisible = true;
        Layout();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    /// <summary>
    /// Shows the browser-owned new-tab page while the active tab has no content host, with recent
    /// pages and bookmarks as shortcuts. <paramref name="entries"/> null keeps the current ones.
    /// </summary>
    public void SetNewTabPage(bool visible, bool isPrivate, StartPageEntries? entries = null)
    {
        var title = _localization.Get(isPrivate ? "start.privateTitle" : "app.name");
        var subtitle = _localization.Get(isPrivate ? "start.privateBody" : "start.subtitle");
        var nextEntries = entries ?? _startEntries;

        // The shell renders state on every source, title, and loading change, so this is called many
        // times per page load. Re-laying out and erasing the whole window for an unchanged page
        // flashes the start-page text and the content edge on every reload.
        if (visible == _startPageVisible &&
            string.Equals(title, _startTitle, StringComparison.Ordinal) &&
            string.Equals(subtitle, _startSubtitle, StringComparison.Ordinal) &&
            nextEntries.SameAs(_startEntries)) return;

        _startPageVisible = visible;
        _startTitle = title;
        _startSubtitle = subtitle;
        _startEntries = nextEntries;
        RenderStartPage();
    }

    /// <summary>The new-tab page's shortcut URLs in order (recent, then bookmarks); for the smoke harness.</summary>
    public IReadOnlyList<string> StartEntryUrls =>
        _startEntryUrls.Where((url, index) => !string.IsNullOrEmpty(url) &&
            NativeMethods.IsWindowVisible(_startEntryButtons[index])).ToList();

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

    /// <summary>Raised after the palette changed, so dependent surfaces can follow it.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>The content-area background the palette paints behind the WebView2 surface.</summary>
    public uint ContentBackgroundColor => _theme.Palette.Surface;

    // ---------- Window procedure ----------

    private IntPtr OnWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case NativeMethods.WM_CREATE:
                    try
                    {
                        CreateControls(hWnd);
                    }
                    catch (Exception exception)
                    {
                        // A half-created window is worse than no window: abort creation so Create()
                        // reports the failure instead of leaving missing controls behind.
                        Trace.TraceError("The browser controls could not be created: {0}", exception);
                        return new IntPtr(-1);
                    }

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
                    // Windows broadcasts this for unrelated settings too; only a palette change
                    // re-resolves the system palette, which raises Changed just on a real change,
                    // so a theme switch never repaints in steps.
                    if (IsPaletteChange(lParam)) _themeSource.RefreshSystem();
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
                    var timerId = NativeMethods.GetLoWord(wParam);
                    if (timerId == ShutdownGuardTimerId)
                    {
                        // Shutdown is asynchronous, but a shell that never completes it must not leave
                        // a window that cannot be closed.
                        Trace.TraceWarning("The browser did not finish shutting down; forcing the window closed.");
                        CompleteShutdown();
                        return IntPtr.Zero;
                    }

                    TimerTick?.Invoke(this, timerId);
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

                case NativeMethods.WM_QUERYENDSESSION:
                    // Windows is ending the session and will not wait for asynchronous shutdown.
                    // Save synchronously; the default answer keeps the browser from blocking logoff.
                    SessionEndingRequested?.Invoke(this, EventArgs.Empty);
                    return new IntPtr(1);

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
            control == _startSubtitleText || control == _startRecentHeader || control == _startBookmarksHeader
                ? (palette.SecondaryText, palette.Surface) :
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
        _startRecentHeader = CreateChild(hWnd, "STATIC", IdStartRecentHeader,
            NativeMethods.WS_CHILD | NativeMethods.SS_LEFTNOWORDWRAP | NativeMethods.SS_NOPREFIX);
        _startBookmarksHeader = CreateChild(hWnd, "STATIC", IdStartBookmarksHeader,
            NativeMethods.WS_CHILD | NativeMethods.SS_LEFTNOWORDWRAP | NativeMethods.SS_NOPREFIX);
        for (var slot = 0; slot < _startEntryButtons.Length; slot++)
        {
            _startEntryButtons[slot] = CreateButton(hWnd, IdStartEntryFirst + slot, FlatButtonStyle.Link,
                FlatBackdrop.Surface, visible: false);
            _startEntryUrls[slot] = string.Empty;
        }

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
        foreach (var control in _startEntryButtons.Concat(new[] { _startRecentHeader, _startBookmarksHeader }))
        {
            if (control != IntPtr.Zero) NativeMethods.SetWindowFont(control, _fonts.Body, false);
        }

        _buttons.Refresh();
        _fields.Refresh(Scale(NativeMetrics.ControlHeight));
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
        FillStartEntries(0, _startEntries.Recent);
        FillStartEntries(StartEntrySlots, _startEntries.Bookmarks);
        Layout();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    private void FillStartEntries(int firstSlot, IReadOnlyList<StartPageEntry> entries)
    {
        for (var slot = 0; slot < StartEntrySlots; slot++)
        {
            var entry = slot < entries.Count ? entries[slot] : null;
            var button = _startEntryButtons[firstSlot + slot];
            _startEntryUrls[firstSlot + slot] = entry?.Url ?? string.Empty;
            NativeMethods.SetWindowText(button, entry?.Title ?? string.Empty);
            // The tooltip names the address, which the row's title alone may not show.
            if (entry is not null) _buttons.SetTooltip(button, entry.Url);
        }
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
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _addressEdited = false;
                        NavigateRequested?.Invoke(this, text);
                    }
                }
            }
            else if (notification == NativeMethods.CBN_EDITCHANGE || notification == NativeMethods.EN_CHANGE)
            {
                if (!_updatingAddressText) _addressEdited = true;
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
                if (_loading) StopRequested?.Invoke(this, EventArgs.Empty);
                else ReloadRequested?.Invoke(this, EventArgs.Empty);
                break;
            case >= IdStartEntryFirst and < IdStartEntryFirst + StartEntrySlots * 2:
                var url = _startEntryUrls[commandId - IdStartEntryFirst];
                if (!string.IsNullOrEmpty(url)) StartEntryRequested?.Invoke(this, url);
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
            case IdDownloads:
                OpenPanelRequested?.Invoke(this, "downloads");
                break;
            case IdBookmarks:
                OpenPanelRequested?.Invoke(this, "bookmarks");
                break;
            case IdCookies:
                OpenPanelRequested?.Invoke(this, "cookies");
                break;
            case IdBrowsingData:
                OpenPanelRequested?.Invoke(this, "data");
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
        var commands = new[]
        {
            IdNewTab, IdNewPrivateTab, IdHistory, IdDownloads, IdBookmarks, IdCookies, IdBrowsingData, IdSettings,
        };
        var items = new[]
        {
            new FlatPopupItem(_localization.Get("tab.newAction"), "Ctrl+T"),
            new FlatPopupItem(_localization.Get("tab.newPrivateAction"), "Ctrl+Shift+N"),
            new FlatPopupItem(_localization.Get("toolbar.history"), "Ctrl+H", separatorBefore: true),
            new FlatPopupItem(_localization.Get("toolbar.downloads"), "Ctrl+J"),
            new FlatPopupItem(_localization.Get("toolbar.bookmarks")),
            new FlatPopupItem(_localization.Get("toolbar.cookies"), separatorBefore: true),
            new FlatPopupItem(_localization.Get("toolbar.browsingData"), "Ctrl+Shift+Del"),
            new FlatPopupItem(_localization.Get("toolbar.settings"), separatorBefore: true),
        };
        NativeMethods.GetWindowRect(_menuButton, out var anchor);
        _menu.Show(_hwnd, _menuButton, anchor, items,
            chosen: index => InvokeCommand(commands[index]),
            alignRight: true,
            minimumWidth: Scale(240));
    }

    /// <summary>The commands a tab's context menu offers, in the order it lists them.</summary>
    private static readonly TabCommandKind[] TabMenuCommands =
    {
        TabCommandKind.Reload, TabCommandKind.Duplicate,
        TabCommandKind.Close, TabCommandKind.CloseOthers, TabCommandKind.CloseToTheRight,
    };

    /// <summary>
    /// Opens a tab's context menu at a screen point. Commands that would do nothing (closing the
    /// others when it is the only tab) are left out rather than shown disabled.
    /// </summary>
    public void ShowTabMenu(int index, NativeMethods.POINT screenPoint)
    {
        if (index < 0 || index >= _tabMirror.Count) return;
        var tabId = _tabMirror[index].Id;
        var commands = TabMenuCommands
            .Where(kind => kind switch
            {
                TabCommandKind.CloseOthers => _tabMirror.Count > 1,
                TabCommandKind.CloseToTheRight => index < _tabMirror.Count - 1,
                _ => true,
            })
            .ToList();
        var items = commands.Select(kind => kind switch
        {
            TabCommandKind.Reload => new FlatPopupItem(_localization.Get("tab.menu.reload")),
            TabCommandKind.Duplicate => new FlatPopupItem(_localization.Get("tab.menu.duplicate")),
            TabCommandKind.Close => new FlatPopupItem(_localization.Get("tab.menu.close"), "Ctrl+W", separatorBefore: true),
            TabCommandKind.CloseOthers => new FlatPopupItem(_localization.Get("tab.menu.closeOthers")),
            _ => new FlatPopupItem(_localization.Get("tab.menu.closeRight")),
        }).ToList();

        var anchor = new NativeMethods.RECT
        {
            left = screenPoint.x, top = screenPoint.y, right = screenPoint.x, bottom = screenPoint.y,
        };
        _menu.Show(_hwnd, _tabStrip, anchor, items,
            chosen: chosen => TabCommandRequested?.Invoke(this, new TabCommand(tabId, commands[chosen])),
            minimumWidth: Scale(200));
    }

    /// <summary>True when a WM_SETTINGCHANGE broadcast reports a system palette change.</summary>
    private static bool IsPaletteChange(IntPtr lParam)
    {
        var section = lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam);
        return string.Equals(section, "ImmersiveColorSet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(section, "Accessibility", StringComparison.OrdinalIgnoreCase);
    }

    private void PopulateAddressSuggestions()
    {
        var suggestions = AddressSuggestionsProvider?.Invoke();
        if (suggestions is null) return;

        // Resetting the list clears the edit text; restore what the user has typed, not the last
        // committed URL. Otherwise opening the drop-down erases an in-progress address.
        var draft = NativeMethods.GetWindowText(_addressCombo);
        _updatingAddressText = true;
        try
        {
            NativeMethods.SendMessage(_addressCombo, NativeMethods.CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
            foreach (var suggestion in suggestions)
            {
                NativeMethods.SendMessage(_addressCombo, NativeMethods.CB_ADDSTRING, IntPtr.Zero, suggestion);
            }

            NativeMethods.SetWindowText(_addressCombo, draft);
        }
        finally
        {
            _updatingAddressText = false;
        }
    }

    private void OnNotify(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;
        var header = Marshal.PtrToStructure<NativeMethods.NMHDR>(lParam);
        if (header.hwndFrom != _tabStrip) return;

        if (header.code == NativeMethods.TCN_SELCHANGE)
        {
            var index = SelectedTabIndex;
            EnsureTabVisible(index);
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
