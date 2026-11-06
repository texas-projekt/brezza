using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Brezza.App.Native;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Navigation;
using Brezza.Core.Privacy;
using Brezza.Persistence;

namespace Brezza.App;

internal enum InternalPanelMode
{
    Settings,
    History,
    Bookmarks,
    Cookies,
    BrowsingData,
    Downloads,
}

/// <summary>One row of the history, bookmarks, cookies, browsing-data, or downloads panel. A row
/// carries what it acts on: a page to open, a cookie to delete, a category of browsing data to
/// clear, or a download to open or remove.</summary>
internal sealed class PanelEntry
{
    public PanelEntry(string title, string url, BrowserCookie? cookie = null, BrowserBrowsingDataKind? dataKind = null, BrowserDownload? download = null)
    {
        Title = title;
        Url = url;
        Cookie = cookie;
        DataKind = dataKind;
        Download = download;
    }

    public string Title { get; }

    public string Url { get; }

    /// <summary>The cookie this row manages, for the cookies panel.</summary>
    public BrowserCookie? Cookie { get; }

    /// <summary>The browsing-data category this row clears, for the browsing-data panel.</summary>
    public BrowserBrowsingDataKind? DataKind { get; }

    /// <summary>The download this row manages, for the downloads panel.</summary>
    public BrowserDownload? Download { get; }
}

/// <summary>The row button a point in a list row is on.</summary>
internal enum PanelRowButton
{
    None,

    /// <summary>Opens the folder a download was written to.</summary>
    Folder,

    /// <summary>Removes the row's own entry.</summary>
    Remove,

    /// <summary>Pauses a running download.</summary>
    Pause,

    /// <summary>Resumes a paused download.</summary>
    Resume,

    /// <summary>Cancels a download and drops its partial file.</summary>
    Cancel,
}

/// <summary>Settings the user submitted from the native settings panel.</summary>
internal readonly struct SettingsSubmission
{
    public SettingsSubmission(string searchTemplate, string language, BrowserThemeMode theme,
        BrowserStartupBehavior startupBehavior, string downloadsFolder)
    {
        SearchTemplate = searchTemplate;
        Language = language;
        Theme = theme;
        StartupBehavior = startupBehavior;
        DownloadsFolder = downloadsFolder;
    }

    public string SearchTemplate { get; }

    public string Language { get; }

    public BrowserThemeMode Theme { get; }

    public BrowserStartupBehavior StartupBehavior { get; }

    /// <summary>The folder downloads are written to; empty means the user's own Downloads folder.</summary>
    public string DownloadsFolder { get; }
}

/// <summary>
/// The native panels that replace the former HTML internal pages. Settings, history, bookmarks,
/// cookies, browsing data, and downloads are ordinary Win32 controls in one dialog owned by the
/// browser window; the panel reports user intent and renders the data it is given. Every mode is a
/// toolbar view without a browser URL of its own.
/// </summary>
/// <remarks>
/// Layout: a heading and subtitle, then either a label/field form with a Save/Cancel footer
/// (settings) or a search field over a two-line list (history, bookmarks, cookies, downloads).
/// List rows act on themselves: a click opens the page or the downloaded file, the row's remove
/// button (or Delete) removes it, and Enter opens the selected row. The browsing-data panel is a
/// list of the four data categories with a Clear all footer. Escape closes the panel; in settings
/// it also discards unsaved changes.
/// </remarks>
internal sealed partial class InternalPanelWindow : IDisposable
{
    public const string WindowClassName = "BrezzaInternalPanel";

    // One window class per process, one static procedure. A per-instance delegate would make every
    // later panel send its messages to the first instance once the class was already registered.
    private static readonly NativeMethods.WndProc ClassProcedure = RouteMessage;
    private static readonly Dictionary<IntPtr, InternalPanelWindow> Instances = new();
    private static InternalPanelWindow? _creating;
    private static bool _registered;

    public const int IdTitle = 2001;
    public const int IdPresetLabel = 2002;
    public const int IdPreset = 2003;
    public const int IdTemplateLabel = 2004;
    public const int IdTemplate = 2005;
    public const int IdLanguageLabel = 2006;
    public const int IdLanguage = 2007;
    public const int IdStatus = 2008;
    public const int IdFilter = 2011;
    public const int IdList = 2012;
    public const int IdClear = 2015;
    public const int IdSave = 2016;
    public const int IdCancel = 2017;
    public const int IdThemeLabel = 2019;
    public const int IdTheme = 2020;
    public const int IdEmpty = 2021;
    public const int IdSubtitle = 2022;
    public const int IdStartupLabel = 2023;
    public const int IdStartup = 2024;
    public const int IdDownloadFolderLabel = 2025;
    public const int IdDownloadFolder = 2026;
    public const int IdDownloadFolderBrowse = 2027;

    // Unscaled metrics at 96 DPI.
    private const int ListRowHeight = 48;
    private const int FooterHeight = 64;
    private const int RemoveButtonSize = 28;
    private const string GlyphRemove = "\uE711";
    private const string GlyphFolder = "\uE8B7";
    private const string GlyphPause = "\uE769";
    private const string GlyphResume = "\uE768";
    private const string GlyphCancel = "\uE894";

    private readonly Localization _localization;
    private readonly IntPtr _owner;
    private readonly NativeThemeSource _themeSource;
    private readonly NativeThemeResources _theme;
    private readonly NativeFonts _fonts = new();
    private readonly FlatButtonSet _buttons;
    private readonly FlatFieldSet _fields;

    private IntPtr _hwnd;
    private uint _dpi = NativeMethods.USER_DEFAULT_SCREEN_DPI;
    private InternalPanelMode _mode = InternalPanelMode.Settings;

    private IntPtr _titleText;
    private IntPtr _subtitleText;
    private IntPtr _presetLabel;
    private IntPtr _presetCombo;
    private IntPtr _templateLabel;
    private IntPtr _templateEdit;
    private IntPtr _languageLabel;
    private IntPtr _languageCombo;
    private IntPtr _themeLabel;
    private IntPtr _themeCombo;
    private IntPtr _startupLabel;
    private IntPtr _startupCombo;
    private IntPtr _downloadFolderLabel;
    private IntPtr _downloadFolderEdit;
    private IntPtr _downloadFolderButton;
    private IntPtr _statusText;
    private IntPtr _filterEdit;
    private IntPtr _listView;
    private IntPtr _emptyText;
    private IntPtr _clearButton;
    private IntPtr _saveButton;
    private IntPtr _cancelButton;

    private readonly NativeMethods.SubClassProc _listSubclass;
    private int _hotRow = -1;
    private PanelRowButton _hotButton;
    private int _pressedRow = -1;

    private readonly List<PanelEntry> _entries = new();
    private readonly List<PanelEntry> _visible = new();
    private string _savedTemplate = string.Empty;
    private string _savedLanguage = string.Empty;
    private BrowserThemeMode _savedTheme = BrowserThemeMode.System;
    private BrowserStartupBehavior _savedStartup = BrowserStartupBehavior.NewTab;

    /// <summary>The downloads folder as it was last saved; empty means the user's own Downloads folder.</summary>
    private string _savedDownloadFolder = string.Empty;
    private string? _statusKey;

    public InternalPanelWindow(Localization localization, IntPtr owner, NativeThemeSource themeSource)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _owner = owner;
        _themeSource = themeSource ?? throw new ArgumentNullException(nameof(themeSource));
        _theme = new NativeThemeResources(_themeSource);
        _buttons = new FlatButtonSet(_theme, _fonts, () => _dpi);
        _fields = new FlatFieldSet(_theme, _fonts, () => _dpi);
        _listSubclass = OnListMessage;
        // The panel follows the shared source, so a theme change repaints it like every other window.
        _themeSource.Changed += OnThemeChanged;
    }

    public event EventHandler<SettingsSubmission>? SaveSettingsRequested;

    /// <summary>The user asked the system to pick a downloads folder; the shell shows the picker.</summary>
    public event EventHandler? BrowseDownloadFolderRequested;
    public event EventHandler<string>? OpenUrlRequested;
    public event EventHandler<string>? RemoveEntryRequested;
    public event EventHandler<BrowserCookie>? RemoveCookieRequested;
    public event EventHandler? ClearHistoryRequested;
    public event EventHandler<BrowserBrowsingDataKind>? ClearBrowsingDataRequested;
    public event EventHandler<string>? OpenDownloadFileRequested;
    public event EventHandler<string>? OpenDownloadFolderRequested;
    public event EventHandler<string>? RemoveDownloadRequested;
    public event EventHandler<string>? PauseDownloadRequested;
    public event EventHandler<string>? ResumeDownloadRequested;
    public event EventHandler<string>? CancelDownloadRequested;
    public event EventHandler? ClearDownloadsRequested;
    public event EventHandler? CloseRequested;

    public bool IsOpen => _hwnd != IntPtr.Zero;

    /// <summary>The panel's own window, which owns the dialogs it opens.</summary>
    public IntPtr Handle => _hwnd;

    private bool IsSettings => _mode == InternalPanelMode.Settings;

    private bool IsCookies => _mode == InternalPanelMode.Cookies;

    private bool IsBrowsingData => _mode == InternalPanelMode.BrowsingData;

    private bool IsDownloads => _mode == InternalPanelMode.Downloads;

    /// <summary>A panel that shows a list of entries: history, bookmarks, cookies, or downloads.</summary>
    private bool IsList => !IsSettings && !IsBrowsingData;

    /// <summary>
    /// A panel whose rows have no page to open: cookies and browsing data act on themselves, so a
    /// click or Enter on a row does nothing and only the row's own button runs. A downloads row
    /// opens its file once the download completed, so it is not self-acting.
    /// </summary>
    private bool IsSelfActing => IsCookies || IsBrowsingData;

    private IntPtr[] SettingsControls => new[]
    {
        _presetLabel, _presetCombo, _templateLabel, _templateEdit, _languageLabel, _languageCombo,
        _themeLabel, _themeCombo, _startupLabel, _startupCombo,
        _downloadFolderLabel, _downloadFolderEdit, _downloadFolderButton,
    };

    /// <summary>Controls both form modes share: the status line and the footer buttons.</summary>
    private IntPtr[] FormControls => new[] { _statusText, _saveButton, _cancelButton };

    private IntPtr[] ListControls => new[] { _filterEdit, _listView, _emptyText };

    // ---------- Presentation ----------

    public void ShowSettings(string template, string language, BrowserThemeMode theme,
        BrowserStartupBehavior startupBehavior, string downloadsFolder)
    {
        _mode = InternalPanelMode.Settings;
        _savedTemplate = template;
        _savedLanguage = string.IsNullOrWhiteSpace(language) ? string.Empty : Localization.Normalize(language);
        _savedTheme = BrowserSettings.NormalizeTheme(theme);
        _savedStartup = BrowserSettings.NormalizeStartupBehavior(startupBehavior);
        _savedDownloadFolder = downloadsFolder ?? string.Empty;
        _statusKey = null;
        EnsureCreated();
        ApplyLanguage();

        NativeMethods.SetWindowText(_templateEdit, template);
        NativeMethods.SetWindowText(_downloadFolderEdit, _savedDownloadFolder);
        SelectLanguage(language);
        SelectPreset(template);
        SelectTheme(_savedTheme);
        SelectStartup(_savedStartup);
        ApplyModeVisibility();
        UpdateDirty();
        Present();
    }

    public void ShowEntries(InternalPanelMode mode, IReadOnlyList<PanelEntry> entries)
    {
        // Refreshing the same list after a removal keeps the search text and the row position, so
        // removing several entries from a filtered list does not start over each time.
        var refresh = _hwnd != IntPtr.Zero && _mode == mode && NativeMethods.IsWindowVisible(_hwnd);
        var previousSelection = refresh ? SelectedIndex() : -1;
        EnsureCreated();
        _mode = mode;
        ApplyLanguage();
        _entries.Clear();
        _entries.AddRange(entries);
        if (!refresh) NativeMethods.SetWindowText(_filterEdit, string.Empty);
        ApplyModeVisibility();
        RebuildList();
        if (previousSelection >= 0 && _visible.Count > 0)
        {
            NativeMethods.SendMessage(_listView, NativeMethods.LB_SETCURSEL,
                new IntPtr(Math.Min(previousSelection, _visible.Count - 1)), IntPtr.Zero);
        }

        if (refresh) return;
        Present();
        // Focus starts in the search field, so typing filters at once and Enter opens the first match.
        NativeMethods.SetFocus(_filterEdit);
    }

    /// <summary>
    /// Fills the downloads-folder field with a folder the user picked. Only a pick is written, so
    /// dismissing the dialog leaves whatever the field already holds.
    /// </summary>
    public void SetDownloadFolder(string folder)
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowText(_downloadFolderEdit, folder ?? string.Empty);
        UpdateDirty();
    }

    /// <summary>Reports the outcome of a settings save attempt.</summary>
    public void SetSettingsResult(bool saved)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (saved)
        {
            // The saved values become the baseline that Cancel restores and later rebuilds keep.
            _savedTemplate = NativeMethods.GetWindowText(_templateEdit).Trim();
            _savedLanguage = LanguageValue();
            _savedTheme = ThemeValue();
            _savedStartup = StartupValue();
            _savedDownloadFolder = DownloadFolderValue();
        }

        SetStatus(saved ? "settings.saved" : "settings.invalid");
        UpdateDirty();
    }

    public void ApplyLanguage()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowText(_hwnd, _localization.Get(TitleKey()));
        NativeMethods.SetWindowText(_titleText, _localization.Get(TitleKey()));
        NativeMethods.SetWindowText(_subtitleText, _localization.Get(_mode switch
        {
            InternalPanelMode.Settings => "settings.subtitle",
            InternalPanelMode.History => "history.subtitle",
            InternalPanelMode.Cookies => "cookies.subtitle",
            InternalPanelMode.Downloads => "downloads.subtitle",
            InternalPanelMode.BrowsingData => "data.subtitle",
            _ => "bookmarks.subtitle",
        }));
        NativeMethods.SetWindowText(_presetLabel, _localization.Get("settings.searchProvider"));
        NativeMethods.SetWindowText(_templateLabel, _localization.Get("settings.searchUrl"));
        NativeMethods.SetWindowText(_languageLabel, _localization.Get("settings.language"));
        NativeMethods.SetWindowText(_themeLabel, _localization.Get("settings.theme"));
        NativeMethods.SetWindowText(_startupLabel, _localization.Get("settings.startup"));
        NativeMethods.SetWindowText(_downloadFolderLabel, _localization.Get("settings.downloadFolder"));
        NativeMethods.SetWindowText(_downloadFolderButton, _localization.Get("settings.downloadFolderBrowse"));
        NativeMethods.SendMessage(_filterEdit, 0x1501 /* EM_SETCUEBANNER */, new IntPtr(1),
            _localization.Get(_mode switch
            {
                InternalPanelMode.Cookies => "cookies.search",
                InternalPanelMode.Downloads => "downloads.search",
                InternalPanelMode.History => "history.search",
                _ => "bookmarks.search",
            }));
        NativeMethods.SetWindowText(_emptyText,
            _localization.Get(_mode switch
            {
                InternalPanelMode.Cookies => "cookies.empty",
                InternalPanelMode.Downloads => "downloads.empty",
                InternalPanelMode.History => "history.empty",
                _ => "bookmarks.empty",
            }));
        NativeMethods.SetWindowText(_clearButton,
            _localization.Get(IsDownloads ? "downloads.clear" : IsCookies ? "cookies.clear" : "history.clear"));
        // In browsing-data mode the footer's primary action clears every category and Cancel closes.
        NativeMethods.SetWindowText(_saveButton,
            _localization.Get(IsBrowsingData ? "data.clearAll" : "settings.save"));
        NativeMethods.SetWindowText(_cancelButton,
            _localization.Get(IsBrowsingData ? "settings.close" : "settings.cancel"));
        NativeMethods.SetWindowText(_statusText, _statusKey is null ? string.Empty : _localization.Get(_statusKey));

        RebuildPresetList();
        RebuildLanguageList();
        RebuildThemeList();
        RebuildStartupList();
        Layout();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    /// <summary>Repaints the panel when the shared theme source resolved a different palette.</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _theme.ReleaseBrushes();
        ApplyControlTheme();
    }

    public void Close()
    {
        if (_hwnd == IntPtr.Zero) return;
        DialogKeyboard.Unregister(_hwnd);
        Instances.Remove(_hwnd);
        // The renderers drop the controls that are about to be destroyed and stay usable for the
        // next time the panel opens.
        _buttons.Dispose();
        _fields.Dispose();
        NativeMethods.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _hotRow = -1;
        _hotButton = PanelRowButton.None;
        _pressedRow = -1;
    }

    public void Dispose()
    {
        Close();
        _buttons.Dispose();
        _fields.Dispose();
        _fonts.Dispose();
        _themeSource.Changed -= OnThemeChanged;
        _theme.Dispose();
    }

    // ---------- Window procedure ----------

    private static IntPtr RouteMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Instances.TryGetValue(hWnd, out var panel) && _creating is { } creating)
        {
            // Messages sent during CreateWindowEx arrive before the handle reaches the caller.
            panel = creating;
            Instances[hWnd] = panel;
        }

        if (msg == 0x0082) Instances.Remove(hWnd); // WM_NCDESTROY
        return panel is null
            ? NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam)
            : panel.OnWindowMessage(hWnd, msg, wParam, lParam);
    }

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
                        // Abort the panel instead of showing a window with missing controls.
                        Trace.TraceError("The panel controls could not be created: {0}", exception);
                        return new IntPtr(-1);
                    }

                    return IntPtr.Zero;

                case NativeMethods.WM_ACTIVATE when NativeMethods.GetLoWord(wParam) == NativeMethods.WA_INACTIVE:
                case NativeMethods.WM_MOVE:
                    // Switching apps or moving the panel dismisses its open drop-down.
                    FlatPopup.Current?.Close();
                    break;

                case NativeMethods.WM_SIZE:
                    FlatPopup.Current?.Close();
                    Layout();
                    NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                    return IntPtr.Zero;

                case NativeMethods.WM_ERASEBKGND:
                    return new IntPtr(1);

                case NativeMethods.WM_PAINT:
                    OnPaint(hWnd);
                    return IntPtr.Zero;

                case NativeMethods.WM_DPICHANGED:
                    ApplyDpiChange((uint)(wParam.ToInt64() & 0xFFFF), lParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_COMMAND when lParam == IntPtr.Zero || NativeMethods.GetLoWord(wParam) <= NativeMethods.IDCANCEL:
                    // Enter and Escape arrive from the dialog keyboard handling as IDOK and IDCANCEL.
                    OnDialogKey(NativeMethods.GetLoWord(wParam));
                    return IntPtr.Zero;

                case NativeMethods.WM_COMMAND:
                    OnCommand(wParam, lParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_MEASUREITEM:
                    return _fields.TryMeasure(lParam) ? new IntPtr(1) : OnMeasureItem(lParam);

                case NativeMethods.WM_DRAWITEM:
                    return _buttons.TryDraw(lParam) || _fields.TryDraw(lParam) || DrawListItem(lParam)
                        ? new IntPtr(1)
                        : IntPtr.Zero;

                case NativeMethods.WM_CTLCOLORSTATIC:
                    var palette = _theme.Palette;
                    var secondary = lParam == _subtitleText || lParam == _statusText || lParam == _emptyText;
                    NativeMethods.SetTextColor(wParam, secondary ? palette.SecondaryText : palette.Text);
                    NativeMethods.SetBkColor(wParam, lParam == _emptyText ? palette.Field : palette.Surface);
                    return _theme.Brush(lParam == _emptyText ? palette.Field : palette.Surface);

                case NativeMethods.WM_CTLCOLOREDIT:
                case NativeMethods.WM_CTLCOLORLISTBOX:
                    if (_fields.TryGetColors(lParam, wParam, out var fieldBrush)) return fieldBrush;
                    NativeMethods.SetTextColor(wParam, _theme.Palette.Text);
                    NativeMethods.SetBkColor(wParam, _theme.Palette.Field);
                    return _theme.Brush(_theme.Palette.Field);

                case NativeMethods.WM_CLOSE:
                    // Closing only hides the panel; the browser owns when it goes away.
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    return IntPtr.Zero;
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("The browser panel message {0} failed: {1}", msg, exception);
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnCommand(IntPtr wParam, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;
        var controlId = NativeMethods.GetLoWord(wParam);
        var notification = NativeMethods.GetHiWord(wParam);

        if (controlId == IdPreset && notification == NativeMethods.CBN_SELCHANGE)
        {
            ApplyPresetSelection();
            UpdateDirty();
            return;
        }

        if ((controlId == IdLanguage || controlId == IdTheme || controlId == IdStartup) && notification == NativeMethods.CBN_SELCHANGE)
        {
            UpdateDirty();
            return;
        }

        if (controlId == IdTemplate && notification == NativeMethods.EN_CHANGE)
        {
            UpdateDirty();
            return;
        }

        if (controlId == IdFilter && notification == NativeMethods.EN_CHANGE)
        {
            RebuildList();
            return;
        }

        if (notification != NativeMethods.BN_CLICKED) return;
        switch (controlId)
        {
            case IdClear:
                ConfirmClear();
                break;
            case IdSave:
                // The footer's primary action is a settings save in one mode and a clear of every
                // browsing-data category in the other; both are the panel's own explicit action.
                if (IsBrowsingData) ConfirmClearAll();
                else SaveSettings();
                break;
            case IdCancel:
                CancelAndClose();
                break;
            case IdDownloadFolderBrowse:
                BrowseDownloadFolderRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnDialogKey(int command)
    {
        if (command == NativeMethods.IDCANCEL)
        {
            if (IsSettings) CancelAndClose();
            else CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (command != NativeMethods.IDOK) return;
        if (IsSettings)
        {
            if (NativeMethods.IsWindowEnabled(_saveButton)) SaveSettings();
            return;
        }

        // Enter never clears browsing data or removes a cookie: the panel's own buttons run those,
        // so a stray keypress cannot change the profile.
        if (IsSelfActing) return;

        // Enter opens the selected row, or the first match while typing in the search field.
        var index = SelectedIndex();
        if (index < 0 && _visible.Count > 0) index = 0;
        Open(index);
    }

    private void OnPaint(IntPtr hWnd)
    {
        var paint = new NativeMethods.PAINTSTRUCT();
        var dc = NativeMethods.BeginPaint(hWnd, ref paint);
        try
        {
            NativeMethods.GetClientRect(hWnd, out var client);
            var palette = _theme.Palette;
            using var scope = NativePaintScope.Begin(dc, "panel");
            NativePaint.Fill(dc, client, palette.Surface, _theme);

            // The list sits inside a flat field frame painted here; edits and combos draw their own.
            if (IsList || IsBrowsingData)
            {
                var frame = ListFrame();
                NativePaint.FillRounded(dc, frame, Scale(NativeMetrics.RadiusControl), palette.Field, _theme);
                NativePaint.FrameRounded(dc, frame, Scale(NativeMetrics.RadiusControl), palette.Border, _theme);
            }

            // A form mode has a footer: the line above it separates the actions from the content.
            if (!IsList)
            {
                NativePaint.Fill(dc, NativePaint.Rect(0, client.Height - Scale(FooterHeight), client.Width, 1), palette.Border, _theme);
            }
        }
        finally
        {
            NativeMethods.EndPaint(hWnd, ref paint);
        }
    }

    // ---------- Controls ----------

    private void EnsureCreated()
    {
        if (_hwnd != IntPtr.Zero) return;
        if (!_registered)
        {
            var wndClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = ClassProcedure,
                hInstance = NativeMethods.GetModuleHandle(null),
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(32512)),
                lpszClassName = WindowClassName,
            };
            if (NativeMethods.RegisterClassEx(ref wndClass) == 0 &&
                Marshal.GetLastWin32Error() != NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new InvalidOperationException(
                    "The panel window class could not be registered (error " + Marshal.GetLastWin32Error() + ").");
            }

            _registered = true;
        }

        // The panel opens on the owner's monitor, so its DPI decides the initial size.
        var ownerDpi = _owner == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(_owner);
        if (ownerDpi > 0) _dpi = ownerDpi;
        _fonts.Create(_dpi);

        var width = Scale(680);
        var height = Scale(560);
        var (x, y) = CenterOverOwner(width, height);
        _creating = this;
        try
        {
            _hwnd = NativeMethods.CreateWindowEx(
                0,
                WindowClassName,
                _localization.Get(TitleKey()),
                // No minimize or maximize box: this is a fixed-purpose panel, and an owned window that
                // minimizes has no taskbar button to come back from.
                NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU |
                NativeMethods.WS_THICKFRAME | NativeMethods.WS_CLIPCHILDREN,
                x, y, width, height,
                _owner,
                IntPtr.Zero,
                NativeMethods.GetModuleHandle(null),
                IntPtr.Zero);
        }
        finally
        {
            _creating = null;
        }
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The browser panel could not be created (error " + Marshal.GetLastWin32Error() + ").");
        }

        Instances[_hwnd] = this;
        ApplyFonts();
        ApplyControlTheme();
        DialogKeyboard.Register(_hwnd);
    }

    private (int X, int Y) CenterOverOwner(int width, int height)
    {
        if (_owner == IntPtr.Zero) return (unchecked((int)0x80000000), unchecked((int)0x80000000));
        if (!NativeMethods.GetWindowRect(_owner, out var owner)) return (unchecked((int)0x80000000), unchecked((int)0x80000000));
        var x = owner.left + Math.Max(0, (owner.Width - width) / 2);
        var y = owner.top + Math.Max(0, (owner.Height - height) / 3);
        return (x, y);
    }

    private void CreateControls(IntPtr hWnd)
    {
        const uint label = NativeMethods.WS_CHILD | NativeMethods.SS_LEFT | NativeMethods.SS_NOPREFIX;
        const uint combo = FlatFieldSet.ListStyle;
        const uint edit = FlatFieldSet.EditStyle;
        _titleText = CreateChild(hWnd, "STATIC", IdTitle, label | NativeMethods.WS_VISIBLE);
        _subtitleText = CreateChild(hWnd, "STATIC", IdSubtitle, label | NativeMethods.WS_VISIBLE);
        _presetLabel = CreateChild(hWnd, "STATIC", IdPresetLabel, label);
        _presetCombo = CreateChild(hWnd, "COMBOBOX", IdPreset, combo);
        _templateLabel = CreateChild(hWnd, "STATIC", IdTemplateLabel, label);
        _templateEdit = CreateChild(hWnd, "EDIT", IdTemplate, edit);
        _languageLabel = CreateChild(hWnd, "STATIC", IdLanguageLabel, label);
        _languageCombo = CreateChild(hWnd, "COMBOBOX", IdLanguage, combo);
        _themeLabel = CreateChild(hWnd, "STATIC", IdThemeLabel, label);
        _themeCombo = CreateChild(hWnd, "COMBOBOX", IdTheme, combo);
        _startupLabel = CreateChild(hWnd, "STATIC", IdStartupLabel, label);
        _startupCombo = CreateChild(hWnd, "COMBOBOX", IdStartup, combo);
        _downloadFolderLabel = CreateChild(hWnd, "STATIC", IdDownloadFolderLabel, label);
        _downloadFolderEdit = CreateChild(hWnd, "EDIT", IdDownloadFolder, edit);
        _downloadFolderButton = CreateButton(hWnd, IdDownloadFolderBrowse, FlatButtonStyle.Subtle);
        _statusText = CreateChild(hWnd, "STATIC", IdStatus, label | NativeMethods.SS_ENDELLIPSIS);
        _filterEdit = CreateChild(hWnd, "EDIT", IdFilter, edit);
        _listView = CreateChild(hWnd, "LISTBOX", IdList,
            NativeMethods.WS_CHILD | NativeMethods.WS_VSCROLL | NativeMethods.LBS_NOTIFY | NativeMethods.LBS_HASSTRINGS |
            NativeMethods.LBS_OWNERDRAWFIXED | NativeMethods.LBS_NOINTEGRALHEIGHT);
        _emptyText = CreateChild(hWnd, "STATIC", IdEmpty, NativeMethods.WS_CHILD | NativeMethods.SS_CENTER | NativeMethods.SS_NOPREFIX);
        _clearButton = CreateButton(hWnd, IdClear, FlatButtonStyle.Subtle);
        _saveButton = CreateButton(hWnd, IdSave, FlatButtonStyle.Primary);
        _cancelButton = CreateButton(hWnd, IdCancel, FlatButtonStyle.Subtle);
        NativeMethods.SetWindowSubclass(_listView, _listSubclass, new UIntPtr(0xA1), IntPtr.Zero);

        foreach (var field in new[] { _presetCombo, _languageCombo, _themeCombo, _startupCombo })
            _fields.RegisterCombo(field, FlatBackdrop.Surface);
        foreach (var field in new[] { _templateEdit, _filterEdit, _downloadFolderEdit })
            _fields.RegisterEdit(field, FlatBackdrop.Surface);
    }

    private IntPtr CreateButton(IntPtr parent, int controlId, FlatButtonStyle style)
    {
        var button = CreateChild(parent, "BUTTON", controlId, NativeMethods.WS_CHILD | NativeMethods.BS_OWNERDRAW);
        _buttons.Register(button, style, FlatBackdrop.Surface);
        return button;
    }

    private IntPtr CreateChild(IntPtr parent, string className, int controlId, uint style)
    {
        if (className == "BUTTON" || className == "EDIT" || className == "COMBOBOX" || className == "LISTBOX")
            style |= NativeMethods.WS_TABSTOP;
        var handle = NativeMethods.CreateWindowEx(
            0, className, string.Empty, style, 0, 0, 0, 0, parent, new IntPtr(controlId),
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The panel control " + className + " (" + controlId + ") could not be created (error " +
                Marshal.GetLastWin32Error() + ").");
        }

        return handle;
    }

    private IEnumerable<IntPtr> AllControls() => new[] { _titleText, _subtitleText, _clearButton }
        .Concat(SettingsControls)
        .Concat(FormControls)
        .Concat(ListControls);

    private void ApplyFonts()
    {
        foreach (var control in AllControls())
        {
            if (control != IntPtr.Zero) NativeMethods.SetWindowFont(control, _fonts.Body, false);
        }

        NativeMethods.SetWindowFont(_titleText, _fonts.Heading, false);
        NativeMethods.SendMessage(_listView, NativeMethods.LB_SETITEMHEIGHT, IntPtr.Zero, new IntPtr(Scale(ListRowHeight)));
        _buttons.Refresh();
        _fields.Refresh(Scale(NativeMetrics.ControlHeight));
    }

    private void ApplyControlTheme()
    {
        if (_hwnd == IntPtr.Zero) return;
        _theme.ApplyToWindowFrame(_hwnd);
        _theme.ApplyToControl(_listView);
        _buttons.Refresh();
        _fields.Refresh(Scale(NativeMetrics.ControlHeight));
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
        // Repaint the panel and all its controls now, in the same pass as the main window.
        NativeMethods.RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, NativeMethods.RDW_REPAINT_ALL_NOW);
    }

    private void ApplyDpiChange(uint dpi, IntPtr suggestedRect)
    {
        _dpi = dpi == 0 ? NativeMethods.USER_DEFAULT_SCREEN_DPI : dpi;
        _fonts.Create(_dpi);
        ApplyFonts();
        if (suggestedRect != IntPtr.Zero)
        {
            var rect = Marshal.PtrToStructure<NativeMethods.RECT>(suggestedRect);
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, rect.left, rect.top, rect.Width, rect.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        Layout();
    }

}
