using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Brezza.App.Native;
using Brezza.Core.Navigation;
using Brezza.Persistence;

namespace Brezza.App;

internal enum InternalPanelMode
{
    Settings,
    History,
    Bookmarks,
}

/// <summary>One row of the history or bookmarks panel.</summary>
internal readonly struct PanelEntry
{
    public PanelEntry(string title, string url)
    {
        Title = title;
        Url = url;
    }

    public string Title { get; }

    public string Url { get; }
}

/// <summary>Settings the user submitted from the native settings panel.</summary>
internal readonly struct SettingsSubmission
{
    public SettingsSubmission(string searchTemplate, string language, BrowserThemeMode theme)
    {
        SearchTemplate = searchTemplate;
        Language = language;
        Theme = theme;
    }

    public string SearchTemplate { get; }

    public string Language { get; }

    public BrowserThemeMode Theme { get; }
}

/// <summary>
/// The native panel that replaces the former HTML internal pages. Settings, history, and
/// bookmarks are ordinary Win32 controls in one dialog owned by the browser window; the panel
/// reports user intent and renders the data it is given.
/// </summary>
/// <remarks>
/// Layout: a heading and subtitle, then either a label/field form with a Save/Cancel footer
/// (settings) or a search field over a two-line list (history, bookmarks). List rows act on
/// themselves: a click opens the page, the row's remove button (or Delete) removes it, and Enter
/// opens the selected row. Escape closes the panel; in settings it also discards unsaved changes.
/// </remarks>
internal sealed class InternalPanelWindow : IDisposable
{
    public const string WindowClassName = "BrezzaInternalPanel";

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

    // Unscaled metrics at 96 DPI.
    private const int Margin = 24;
    private const int FieldHeight = 32;
    private const int RowGap = 16;
    private const int ListRowHeight = 48;
    private const int FooterHeight = 64;
    private const int RemoveButtonSize = 28;
    private const string GlyphRemove = "\uE711";

    private readonly NativeMethods.WndProc _wndProc;
    private readonly Localization _localization;
    private readonly IntPtr _owner;
    private readonly NativeThemeResources _theme = new();
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
    private IntPtr _statusText;
    private IntPtr _filterEdit;
    private IntPtr _listView;
    private IntPtr _emptyText;
    private IntPtr _clearButton;
    private IntPtr _saveButton;
    private IntPtr _cancelButton;

    private readonly NativeMethods.SubClassProc _listSubclass;
    private int _hotRow = -1;
    private bool _hotRemove;
    private int _pressedRow = -1;

    private readonly List<PanelEntry> _entries = new();
    private readonly List<PanelEntry> _visible = new();
    private string _savedTemplate = string.Empty;
    private string _savedLanguage = string.Empty;
    private BrowserThemeMode _savedTheme = BrowserThemeMode.System;
    private string? _statusKey;

    public InternalPanelWindow(Localization localization, IntPtr owner)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _owner = owner;
        _wndProc = OnWindowMessage;
        _buttons = new FlatButtonSet(_theme, _fonts, () => _dpi);
        _fields = new FlatFieldSet(_theme, _fonts, () => _dpi);
        _listSubclass = OnListMessage;
    }

    public event EventHandler<SettingsSubmission>? SaveSettingsRequested;
    public event EventHandler<string>? OpenUrlRequested;
    public event EventHandler<string>? RemoveEntryRequested;
    public event EventHandler? ClearHistoryRequested;
    public event EventHandler? CloseRequested;

    public bool IsOpen => _hwnd != IntPtr.Zero;

    private bool IsSettings => _mode == InternalPanelMode.Settings;

    private IntPtr[] SettingsControls => new[]
    {
        _presetLabel, _presetCombo, _templateLabel, _templateEdit, _languageLabel, _languageCombo,
        _themeLabel, _themeCombo, _statusText, _saveButton, _cancelButton,
    };

    private IntPtr[] ListControls => new[] { _filterEdit, _listView, _emptyText };

    // ---------- Presentation ----------

    public void ShowSettings(string template, string language, BrowserThemeMode theme)
    {
        _mode = InternalPanelMode.Settings;
        _savedTemplate = template;
        _savedLanguage = language;
        _savedTheme = BrowserSettings.NormalizeTheme(theme);
        _statusKey = null;
        EnsureCreated();
        ApplyLanguage();

        NativeMethods.SetWindowText(_templateEdit, template);
        SelectLanguage(language);
        SelectPreset(template);
        SelectTheme(_savedTheme);
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
        }

        SetStatus(saved ? "settings.saved" : "settings.invalid");
        UpdateDirty();
    }

    public void ApplyLanguage()
    {
        if (_hwnd == IntPtr.Zero) return;
        var history = _mode == InternalPanelMode.History;
        NativeMethods.SetWindowText(_hwnd, _localization.Get(TitleKey()));
        NativeMethods.SetWindowText(_titleText, _localization.Get(TitleKey()));
        NativeMethods.SetWindowText(_subtitleText, _localization.Get(_mode switch
        {
            InternalPanelMode.Settings => "settings.subtitle",
            InternalPanelMode.History => "history.subtitle",
            _ => "bookmarks.subtitle",
        }));
        NativeMethods.SetWindowText(_presetLabel, _localization.Get("settings.searchProvider"));
        NativeMethods.SetWindowText(_templateLabel, _localization.Get("settings.searchUrl"));
        NativeMethods.SetWindowText(_languageLabel, _localization.Get("settings.language"));
        NativeMethods.SetWindowText(_themeLabel, _localization.Get("settings.theme"));
        NativeMethods.SendMessage(_filterEdit, 0x1501 /* EM_SETCUEBANNER */, new IntPtr(1),
            _localization.Get(history ? "history.search" : "bookmarks.search"));
        NativeMethods.SetWindowText(_emptyText, _localization.Get(history ? "history.empty" : "bookmarks.empty"));
        NativeMethods.SetWindowText(_clearButton, _localization.Get("history.clear"));
        NativeMethods.SetWindowText(_saveButton, _localization.Get("settings.save"));
        NativeMethods.SetWindowText(_cancelButton, _localization.Get("settings.cancel"));
        NativeMethods.SetWindowText(_statusText, _statusKey is null ? string.Empty : _localization.Get(_statusKey));

        RebuildPresetList();
        RebuildLanguageList();
        RebuildThemeList();
        Layout();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    public void ApplyTheme(BrowserThemeMode mode)
    {
        _theme.Apply(mode);
        ApplyControlTheme();
    }

    public void Close()
    {
        if (_hwnd == IntPtr.Zero) return;
        DialogKeyboard.Unregister(_hwnd);
        // The renderers drop the controls that are about to be destroyed and stay usable for the
        // next time the panel opens.
        _buttons.Dispose();
        _fields.Dispose();
        NativeMethods.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _hotRow = -1;
        _pressedRow = -1;
    }

    public void Dispose()
    {
        Close();
        _buttons.Dispose();
        _fields.Dispose();
        _fonts.Dispose();
        _theme.Dispose();
    }

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

        if ((controlId == IdLanguage || controlId == IdTheme) && notification == NativeMethods.CBN_SELCHANGE)
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
                SaveSettings();
                break;
            case IdCancel:
                CancelAndClose();
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
            if (!IsSettings)
            {
                var frame = ListFrame();
                NativePaint.FillRounded(dc, frame, Scale(4), palette.Field, _theme);
                NativePaint.FrameRounded(dc, frame, Scale(4), palette.Border, _theme);
            }
            else
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
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = NativeMethods.GetModuleHandle(null),
            hCursor = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(32512)),
            lpszClassName = WindowClassName,
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        // The panel opens on the owner's monitor, so its DPI decides the initial size.
        var ownerDpi = _owner == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(_owner);
        if (ownerDpi > 0) _dpi = ownerDpi;
        _fonts.Create(_dpi);

        var width = Scale(680);
        var height = Scale(560);
        var (x, y) = CenterOverOwner(width, height);
        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            _localization.Get(TitleKey()),
            NativeMethods.WS_OVERLAPPEDWINDOW | NativeMethods.WS_CLIPCHILDREN,
            x, y, width, height,
            _owner,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The browser panel could not be created (error " + Marshal.GetLastWin32Error() + ").");
        }

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

        foreach (var field in new[] { _presetCombo, _languageCombo, _themeCombo })
            _fields.RegisterCombo(field, FlatBackdrop.Surface);
        foreach (var field in new[] { _templateEdit, _filterEdit })
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
        _fields.Refresh(Scale(FieldHeight));
    }

    private void ApplyControlTheme()
    {
        if (_hwnd == IntPtr.Zero) return;
        _theme.ApplyToWindowFrame(_hwnd);
        _theme.ApplyToControl(_listView);
        _buttons.Refresh();
        _fields.Refresh(Scale(FieldHeight));
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
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

    // ---------- Settings ----------

    private void RebuildPresetList()
    {
        var items = SearchProviderPresets.All.Select(preset => preset.Name).Concat(new[] { _localization.Get("settings.custom") });
        var selected = (int)NativeMethods.SendMessage(_presetCombo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        FillCombo(_presetCombo, items, selected);
    }

    // Relabeling a combo keeps the user's current choice, so re-applying the language after a save
    // cannot silently revert a selection that was just saved.
    private void RebuildLanguageList()
    {
        FillCombo(_languageCombo, new[]
        {
            _localization.Get("settings.languageSystem"),
            _localization.Get("settings.languageEnglish"),
            _localization.Get("settings.languageChinese"),
        }, CurrentSelection(_languageCombo, LanguageIndex(_savedLanguage)));
    }

    private void RebuildThemeList()
    {
        FillCombo(_themeCombo, new[]
        {
            _localization.Get("settings.themeSystem"),
            _localization.Get("settings.themeLight"),
            _localization.Get("settings.themeDark"),
        }, CurrentSelection(_themeCombo, ThemeIndex(_savedTheme)));
    }

    private static int CurrentSelection(IntPtr combo, int fallback)
    {
        var selected = (int)NativeMethods.SendMessage(combo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return selected >= 0 ? selected : fallback;
    }

    private static void FillCombo(IntPtr combo, IEnumerable<string> items, int selected)
    {
        if (combo == IntPtr.Zero) return;
        NativeMethods.SendMessage(combo, NativeMethods.CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        foreach (var item in items) NativeMethods.SendMessage(combo, NativeMethods.CB_ADDSTRING, IntPtr.Zero, item);
        NativeMethods.SendMessage(combo, NativeMethods.CB_SETCURSEL, new IntPtr(selected), IntPtr.Zero);
    }

    private void SelectLanguage(string language) =>
        NativeMethods.SendMessage(_languageCombo, NativeMethods.CB_SETCURSEL, new IntPtr(LanguageIndex(language)), IntPtr.Zero);

    private void SelectTheme(BrowserThemeMode theme) =>
        NativeMethods.SendMessage(_themeCombo, NativeMethods.CB_SETCURSEL, new IntPtr(ThemeIndex(theme)), IntPtr.Zero);

    private void SelectPreset(string template)
    {
        var presets = SearchProviderPresets.All;
        var index = presets.Select((preset, i) => (preset, i))
            .FirstOrDefault(pair => string.Equals(pair.preset.Template, template, StringComparison.OrdinalIgnoreCase));
        var presetIndex = index.preset is null ? presets.Count : index.i;
        NativeMethods.SendMessage(_presetCombo, NativeMethods.CB_SETCURSEL, new IntPtr(presetIndex), IntPtr.Zero);
    }

    private static int ThemeIndex(BrowserThemeMode theme) => theme switch
    {
        BrowserThemeMode.Light => 1,
        BrowserThemeMode.Dark => 2,
        _ => 0,
    };

    /// <summary>Index of a language value in the settings combo: system, English, Chinese.</summary>
    private static int LanguageIndex(string? language) => language switch
    {
        Localization.Chinese => 2,
        Localization.English => 1,
        _ => 0,
    };

    private void ApplyPresetSelection()
    {
        var index = (int)NativeMethods.SendMessage(_presetCombo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        var presets = SearchProviderPresets.All;
        if (index < 0 || index >= presets.Count)
        {
            // "Custom" keeps whatever the user types.
            SetStatus("settings.customHint");
            return;
        }

        NativeMethods.SetWindowText(_templateEdit, presets[index].Template);
        SetStatus(null);
    }

    private void SaveSettings()
    {
        var template = NativeMethods.GetWindowText(_templateEdit).Trim();
        SaveSettingsRequested?.Invoke(this, new SettingsSubmission(template, LanguageValue(), ThemeValue()));
    }

    /// <summary>Cancel works like a dialog's Cancel: unsaved changes are dropped and the panel closes.</summary>
    private void CancelAndClose()
    {
        NativeMethods.SetWindowText(_templateEdit, _savedTemplate);
        SelectLanguage(_savedLanguage);
        SelectTheme(_savedTheme);
        SelectPreset(_savedTemplate);
        SetStatus(null);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Save is available only when something differs from the saved settings.</summary>
    private void UpdateDirty()
    {
        if (_saveButton == IntPtr.Zero) return;
        var dirty =
            !string.Equals(NativeMethods.GetWindowText(_templateEdit).Trim(), _savedTemplate, StringComparison.Ordinal) ||
            !string.Equals(LanguageValue(), _savedLanguage, StringComparison.Ordinal) ||
            ThemeValue() != _savedTheme;
        NativeMethods.EnableWindow(_saveButton, dirty);
        // A pending change replaces the last save result, which no longer describes the form.
        if (dirty && _statusKey == "settings.saved") SetStatus(null);
    }

    private string LanguageValue()
    {
        var index = (int)NativeMethods.SendMessage(_languageCombo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return index switch
        {
            2 => Localization.Chinese,
            1 => Localization.English,
            _ => string.Empty,
        };
    }

    private BrowserThemeMode ThemeValue()
    {
        var index = (int)NativeMethods.SendMessage(_themeCombo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return index switch
        {
            1 => BrowserThemeMode.Light,
            2 => BrowserThemeMode.Dark,
            _ => BrowserThemeMode.System,
        };
    }

    private void SetStatus(string? key)
    {
        _statusKey = key;
        NativeMethods.SetWindowText(_statusText, key is null ? string.Empty : _localization.Get(key));
    }

    // ---------- History and bookmarks ----------

    private void RebuildList()
    {
        if (_listView == IntPtr.Zero) return;
        var filter = NativeMethods.GetWindowText(_filterEdit).Trim();
        _visible.Clear();
        _visible.AddRange(string.IsNullOrEmpty(filter)
            ? _entries
            : _entries.Where(entry =>
                entry.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                entry.Url.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0));

        NativeMethods.SendMessage(_listView, NativeMethods.LB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        foreach (var entry in _visible)
        {
            // The string keeps the row readable to assistive technology; the row is painted from _visible.
            var label = string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title + " — " + entry.Url;
            NativeMethods.SendMessage(_listView, NativeMethods.LB_ADDSTRING, IntPtr.Zero, label);
        }

        var history = _mode == InternalPanelMode.History;
        NativeMethods.SetWindowText(_emptyText, _localization.Get(
            _entries.Count > 0 ? "list.noMatches" : history ? "history.empty" : "bookmarks.empty"));
        var empty = _visible.Count == 0;
        NativeMethods.ShowWindow(_emptyText, empty ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        NativeMethods.ShowWindow(_listView, empty ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOW);
        _hotRow = -1;
        UpdateListActions();
    }

    private IntPtr OnMeasureItem(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return IntPtr.Zero;
        var item = Marshal.PtrToStructure<NativeMethods.MEASUREITEMSTRUCT>(lParam);
        if (item.CtlType != NativeMethods.ODT_LISTBOX) return IntPtr.Zero;
        item.itemHeight = (uint)Scale(ListRowHeight);
        Marshal.StructureToPtr(item, lParam, false);
        return new IntPtr(1);
    }

    /// <summary>Paints a two-line history or bookmark row: the page title over its address.</summary>
    private bool DrawListItem(IntPtr lParam)
    {
        var item = Marshal.PtrToStructure<NativeMethods.DRAWITEMSTRUCT>(lParam);
        if (item.CtlType != NativeMethods.ODT_LISTBOX || item.hwndItem != _listView) return false;

        var palette = _theme.Palette;
        var rect = item.rcItem;
        using var paint = NativePaintScope.Begin(item.hDC, "list row");
        NativePaint.Fill(item.hDC, rect, palette.Field, _theme);
        if ((int)item.itemID < 0 || item.itemID >= _visible.Count) return true;

        if ((item.itemState & NativeMethods.ODS_SELECTED) != 0)
        {
            var selection = rect;
            selection.left += Scale(4);
            selection.right -= Scale(4);
            selection.top += Scale(2);
            selection.bottom -= Scale(2);
            NativePaint.FillRounded(item.hDC, selection, Scale(4), palette.Selection, _theme);
        }

        var entry = _visible[(int)item.itemID];
        var row = (int)item.itemID;
        var text = rect;
        text.left += Scale(14);
        // Space for the remove button is always reserved, so text does not shift on hover.
        text.right = RemoveButtonRect(rect).left - Scale(8);
        if (row == _hotRow || (item.itemState & NativeMethods.ODS_SELECTED) != 0)
        {
            var button = RemoveButtonRect(rect);
            if (row == _hotRow && _hotRemove) NativePaint.FillRounded(item.hDC, button, Scale(4), palette.Hover, _theme);
            NativePaint.Text(item.hDC, GlyphRemove, button, _fonts.SmallIcon, palette.SecondaryText,
                NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        }

        const uint line = NativeMethods.DT_LEFT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_END_ELLIPSIS;
        if (string.IsNullOrWhiteSpace(entry.Title))
        {
            NativePaint.Text(item.hDC, entry.Url, text, _fonts.Body, palette.Text, line | NativeMethods.DT_VCENTER);
            return true;
        }

        var half = rect.Height / 2;
        var title = text;
        title.bottom = rect.top + half + Scale(1);
        var url = text;
        url.top = rect.top + half + Scale(1);
        NativePaint.Text(item.hDC, entry.Title, title, _fonts.Body, palette.Text, line | NativeMethods.DT_BOTTOM);
        NativePaint.Text(item.hDC, entry.Url, url, _fonts.Body, palette.SecondaryText, line);
        return true;
    }

    private void UpdateListActions()
    {
        if (_clearButton == IntPtr.Zero) return;
        NativeMethods.EnableWindow(_clearButton, _entries.Count > 0 && _mode == InternalPanelMode.History);
    }

    private int SelectedIndex() =>
        (int)NativeMethods.SendMessage(_listView, NativeMethods.LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);

    private void Open(int index)
    {
        if (index < 0 || index >= _visible.Count) return;
        OpenUrlRequested?.Invoke(this, _visible[index].Url);
    }

    private void Remove(int index)
    {
        if (index < 0 || index >= _visible.Count) return;
        RemoveEntryRequested?.Invoke(this, _visible[index].Url);
    }

    /// <summary>The remove button inside the right edge of a list row, in list-box coordinates.</summary>
    private NativeMethods.RECT RemoveButtonRect(NativeMethods.RECT row)
    {
        var size = Scale(RemoveButtonSize);
        return NativePaint.Rect(row.right - size - Scale(10), row.top + (row.Height - size) / 2, size, size);
    }

    private NativeMethods.RECT RowRect(int index)
    {
        var rect = new NativeMethods.RECT();
        NativeMethods.SendMessage(_listView, NativeMethods.LB_GETITEMRECT, new IntPtr(index), ref rect);
        return rect;
    }

    /// <summary>The row under a list-box client point, and whether the point is on its remove button.</summary>
    private (int Row, bool OnRemove) HitTestRow(IntPtr lParam)
    {
        var result = NativeMethods.SendMessage(_listView, NativeMethods.LB_ITEMFROMPOINT, IntPtr.Zero, lParam).ToInt64();
        var index = (int)(result & 0xFFFF);
        if ((result >> 16) != 0 || index >= _visible.Count) return (-1, false);
        var x = unchecked((short)NativeMethods.GetLoWord(lParam));
        var y = unchecked((short)NativeMethods.GetHiWord(lParam));
        var button = RemoveButtonRect(RowRect(index));
        return (index, x >= button.left && x < button.right && y >= button.top && y < button.bottom);
    }

    private void SetHotRow(int row, bool onRemove)
    {
        if (row == _hotRow && onRemove == _hotRemove) return;
        var previous = _hotRow;
        _hotRow = row;
        _hotRemove = onRemove;
        InvalidateRow(previous);
        if (row != previous) InvalidateRow(row);
    }

    private void InvalidateRow(int row)
    {
        if (row < 0) return;
        var rect = RowRect(row);
        NativeMethods.InvalidateRect(_listView, ref rect, false);
    }

    /// <summary>Row hover, click to open, the per-row remove button, and the Delete key.</summary>
    private IntPtr OnListMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEMOVE:
                var hover = HitTestRow(lParam);
                SetHotRow(hover.Row, hover.OnRemove);
                var track = new NativeMethods.TRACKMOUSEEVENT
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                    dwFlags = NativeMethods.TME_LEAVE,
                    hwndTrack = hWnd,
                };
                NativeMethods.TrackMouseEvent(ref track);
                break;

            case NativeMethods.WM_MOUSELEAVE:
                SetHotRow(-1, false);
                break;

            case NativeMethods.WM_LBUTTONDOWN:
            case NativeMethods.WM_LBUTTONDBLCLK:
                // The list box selects and focuses the row; the action runs on release over the same row.
                _pressedRow = HitTestRow(lParam).Row;
                NativeMethods.DefSubclassProc(hWnd, NativeMethods.WM_LBUTTONDOWN, wParam, lParam);
                return IntPtr.Zero;

            case NativeMethods.WM_LBUTTONUP:
                var result = NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
                var release = HitTestRow(lParam);
                var pressed = _pressedRow;
                _pressedRow = -1;
                if (release.Row >= 0 && release.Row == pressed)
                {
                    if (release.OnRemove) Remove(release.Row);
                    else Open(release.Row);
                }

                return result;

            case NativeMethods.WM_KEYDOWN when NativeMethods.GetLoWord(wParam) == NativeMethods.VK_DELETE:
                Remove(SelectedIndex());
                return IntPtr.Zero;

            case 0x0082: // WM_NCDESTROY
                NativeMethods.RemoveWindowSubclass(hWnd, _listSubclass, new UIntPtr(0xA1));
                break;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ConfirmClear()
    {
        if (_mode != InternalPanelMode.History || _entries.Count == 0) return;
        var answer = NativeMethods.MessageBox(
            _hwnd,
            _localization.Get("history.clearConfirm"),
            _localization.Get("history.clear"),
            NativeMethods.MB_YESNO | NativeMethods.MB_ICONQUESTION);
        if (answer != NativeMethods.IDYES) return;
        ClearHistoryRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---------- Mode and layout ----------

    private string TitleKey() => _mode switch
    {
        InternalPanelMode.Settings => "settings.title",
        InternalPanelMode.History => "history.title",
        _ => "bookmarks.title",
    };

    private void ApplyModeVisibility()
    {
        foreach (var control in SettingsControls)
            NativeMethods.ShowWindow(control, IsSettings ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        foreach (var control in ListControls)
            NativeMethods.ShowWindow(control, IsSettings ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOW);
        NativeMethods.ShowWindow(_clearButton,
            _mode == InternalPanelMode.History ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        Layout();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    private void Present()
    {
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        NativeMethods.UpdateWindow(_hwnd);
        NativeMethods.SetForegroundWindow(_hwnd);
        Layout();
    }

    private int Scale(int value) => NativeMethods.Scale(value, _dpi);

    private void Layout()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.GetClientRect(_hwnd, out var client);
        var margin = Scale(Margin);
        var width = Math.Max(Scale(280), client.Width - margin * 2);
        var y = margin;

        var headingHeight = NativeFonts.Measure(_hwnd, _fonts.Heading, "Ag").y;
        NativeMethods.MoveWindow(_titleText, margin, y, width, headingHeight, true);
        y += headingHeight + Scale(4);
        var bodyHeight = NativeFonts.Measure(_hwnd, _fonts.Body, "Ag").y;
        NativeMethods.MoveWindow(_subtitleText, margin, y, width, bodyHeight, true);
        y += bodyHeight + Scale(24);

        var footerTop = client.Height - Scale(FooterHeight);
        if (IsSettings) LayoutSettings(margin, y, width, bodyHeight);
        else LayoutList(margin, y, width, client.Height - margin);
        if (IsSettings) LayoutFooter(client.Width, footerTop);
        UpdateListActions();
    }

    private void LayoutSettings(int left, int top, int width, int bodyHeight)
    {
        // The label column fits the longest localized label, so translations never clip or overlap.
        var rows = new[]
        {
            (_presetLabel, _presetCombo), (_templateLabel, _templateEdit),
            (_languageLabel, _languageCombo), (_themeLabel, _themeCombo),
        };
        var labelWidth = rows.Max(row => NativeFonts.Measure(_hwnd, _fonts.Body, NativeMethods.GetWindowText(row.Item1)).x);
        labelWidth = Math.Min(labelWidth + Scale(16), width / 2);
        var fieldLeft = left + labelWidth;
        var fieldWidth = width - labelWidth;
        var y = top;
        foreach (var (label, field) in rows)
        {
            var fieldHeight = PlaceField(field, fieldLeft, y, fieldWidth);
            NativeMethods.MoveWindow(label, left, y + (fieldHeight - bodyHeight) / 2, labelWidth, bodyHeight, true);
            y += fieldHeight + Scale(RowGap);
        }

        NativeMethods.MoveWindow(_statusText, fieldLeft, y, fieldWidth, bodyHeight, true);
    }

    private void LayoutList(int left, int top, int width, int bottom)
    {
        // Clearing history is a list-level action, so it sits beside the search field instead of
        // among the row actions.
        var searchWidth = width;
        if (_mode == InternalPanelMode.History)
        {
            var clearWidth = TextButtonWidth(_clearButton);
            searchWidth = width - clearWidth - Scale(8);
            NativeMethods.MoveWindow(_clearButton, left + searchWidth + Scale(8), top, clearWidth, Scale(FieldHeight), true);
        }

        var fieldHeight = PlaceField(_filterEdit, left, top, searchWidth);
        var listTop = top + fieldHeight + Scale(12);
        var listHeight = Math.Max(Scale(96), bottom - listTop);
        var inset = Scale(2);
        NativeMethods.MoveWindow(_listView, left + inset, listTop + inset, width - inset * 2, listHeight - inset * 2, true);
        var emptyHeight = Scale(48);
        NativeMethods.MoveWindow(_emptyText, left + inset, listTop + (listHeight - emptyHeight) / 2,
            width - inset * 2, emptyHeight, true);
    }

    /// <summary>Positions a field control and returns the height of its row.</summary>
    private int PlaceField(IntPtr field, int left, int top, int width)
    {
        var height = Scale(FieldHeight);
        // An edit takes the row height directly; a drop-down list's window height is its open list,
        // and the field renderer sizes the closed field to the row.
        var isEdit = field == _templateEdit || field == _filterEdit;
        NativeMethods.MoveWindow(field, left, top, width, isEdit ? height : Scale(240), true);
        return height;
    }

    private void LayoutFooter(int clientWidth, int footerTop)
    {
        var margin = Scale(Margin);
        var buttonHeight = Scale(32);
        var gap = Scale(8);
        var buttonTop = footerTop + (Scale(FooterHeight) - buttonHeight) / 2;

        // Windows dialog order, right-aligned: the primary action, then Cancel at the far right.
        var right = clientWidth - margin;
        foreach (var button in new[] { _cancelButton, _saveButton })
        {
            var buttonWidth = TextButtonWidth(button);
            right -= buttonWidth;
            NativeMethods.MoveWindow(button, right, buttonTop, buttonWidth, buttonHeight, true);
            right -= gap;
        }
    }

    private int TextButtonWidth(IntPtr button) =>
        Math.Max(Scale(80), NativeFonts.Measure(_hwnd, _fonts.Strong, NativeMethods.GetWindowText(button)).x + Scale(32));

    private NativeMethods.RECT ListFrame()
    {
        NativeMethods.GetWindowRect(_listView, out var rect);
        var topLeft = new NativeMethods.POINT { x = rect.left, y = rect.top };
        NativeMethods.ScreenToClient(_hwnd, ref topLeft);
        var inset = Scale(2);
        return NativePaint.Rect(topLeft.x - inset, topLeft.y - inset, rect.Width + inset * 2, rect.Height + inset * 2);
    }
}
