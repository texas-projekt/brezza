using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Brezza.App.Native;

/// <summary>How a flat button is filled.</summary>
internal enum FlatButtonStyle
{
    /// <summary>Glyph-only toolbar button with a hover fill.</summary>
    Icon,

    /// <summary>Small glyph button, such as the close button inside the active tab.</summary>
    SmallIcon,

    /// <summary>Text button with a hairline border, for secondary actions.</summary>
    Subtle,

    /// <summary>Accent-filled text button, for the primary action of a view.</summary>
    Primary,

    /// <summary>Square window caption button.</summary>
    Caption,

    /// <summary>Square caption close button with a red hover.</summary>
    CaptionClose,
}

/// <summary>The palette role behind a button, painted where the button is not filled.</summary>
internal enum FlatBackdrop
{
    Frame,
    Surface,
    Notice,
}

/// <summary>
/// Owner-drawn, theme-aware rendering for standard <c>BUTTON</c> controls. The buttons stay real
/// Win32 buttons: they keep their localized window text (used for accessibility and tests), raise
/// <c>BN_CLICKED</c>, and take focus; this class only paints them, tracks hover, and owns their tooltips.
/// </summary>
internal sealed class FlatButtonSet : IDisposable
{
    private sealed class Entry
    {
        public FlatButtonStyle Style;
        public FlatBackdrop Backdrop;
        public string? Glyph;
    }

    private readonly Dictionary<IntPtr, Entry> _buttons = new();
    private readonly NativeThemeResources _theme;
    private readonly NativeFonts _fonts;
    private readonly Func<uint> _dpi;
    private readonly NativeMethods.SubClassProc _subclass;
    private readonly HashSet<IntPtr> _tooltipTools = new();
    private IntPtr _tooltip;
    private IntPtr _hot;

    public FlatButtonSet(NativeThemeResources theme, NativeFonts fonts, Func<uint> dpi)
    {
        _theme = theme;
        _fonts = fonts;
        _dpi = dpi;
        _subclass = OnButtonMessage;
    }

    public void Register(IntPtr button, FlatButtonStyle style, FlatBackdrop backdrop, string? glyph = null)
    {
        if (button == IntPtr.Zero) return;
        _buttons[button] = new Entry { Style = style, Backdrop = backdrop, Glyph = glyph };
        NativeMethods.SetWindowSubclass(button, _subclass, new UIntPtr(0xB7), IntPtr.Zero);
    }

    public void SetGlyph(IntPtr button, string glyph)
    {
        if (!_buttons.TryGetValue(button, out var entry) || entry.Glyph == glyph) return;
        entry.Glyph = glyph;
        NativeMethods.InvalidateRect(button, IntPtr.Zero, false);
    }

    /// <summary>Sets the hover tooltip of a button; the tooltip window is created on first use.</summary>
    public void SetTooltip(IntPtr button, string text)
    {
        if (button == IntPtr.Zero) return;
        var owner = NativeMethods.GetParent(button);
        if (_tooltip == IntPtr.Zero)
        {
            _tooltip = NativeMethods.CreateWindowEx(
                NativeMethods.WS_EX_TOPMOST, NativeMethods.TooltipClass, null,
                // No slide or fade: every transient surface in the shell appears at once.
                NativeMethods.WS_POPUP | NativeMethods.TTS_ALWAYSTIP | NativeMethods.TTS_NOPREFIX |
                NativeMethods.TTS_NOANIMATE | NativeMethods.TTS_NOFADE,
                unchecked((int)0x80000000), unchecked((int)0x80000000), unchecked((int)0x80000000), unchecked((int)0x80000000),
                owner, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
            if (_tooltip == IntPtr.Zero) return;
            NativeMethods.SendMessage(_tooltip, NativeMethods.TTM_SETMAXTIPWIDTH, IntPtr.Zero, new IntPtr(480));
            RefreshTooltip();
        }

        var info = new NativeMethods.TOOLINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.TOOLINFO>(),
            uFlags = NativeMethods.TTF_IDISHWND | NativeMethods.TTF_SUBCLASS,
            hwnd = owner,
            uId = button,
            lpszText = text,
        };
        var message = _tooltipTools.Add(button) ? NativeMethods.TTM_ADDTOOL : NativeMethods.TTM_UPDATETIPTEXT;
        NativeMethods.SendMessage(_tooltip, message, IntPtr.Zero, ref info);
    }

    /// <summary>Re-applies fonts and theme to the tooltip and repaints every button.</summary>
    public void Refresh()
    {
        RefreshTooltip();
        foreach (var button in _buttons.Keys) NativeMethods.InvalidateRect(button, IntPtr.Zero, false);
    }

    /// <summary>Paints a registered button from <c>WM_DRAWITEM</c>; returns false for other controls.</summary>
    public bool TryDraw(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return false;
        var item = Marshal.PtrToStructure<NativeMethods.DRAWITEMSTRUCT>(lParam);
        if (item.CtlType != NativeMethods.ODT_BUTTON || !_buttons.TryGetValue(item.hwndItem, out var entry)) return false;

        var palette = _theme.Palette;
        var rect = item.rcItem;
        var disabled = (item.itemState & NativeMethods.ODS_DISABLED) != 0;
        var pressed = (item.itemState & NativeMethods.ODS_SELECTED) != 0;
        var hot = !disabled && item.hwndItem == _hot;
        var focused = (item.itemState & NativeMethods.ODS_FOCUS) != 0 &&
            (item.itemState & NativeMethods.ODS_NOFOCUSRECT) == 0;
        var radius = NativeMethods.Scale(NativeMetrics.RadiusControl, _dpi());

        var backdrop = palette.Backdrop(entry.Backdrop);
        var foreground = palette.TextOn(entry.Backdrop);
        using var paint = NativePaintScope.Begin(item.hDC, "button");
        NativePaint.Fill(item.hDC, rect, backdrop, _theme);

        uint? fill = pressed ? palette.Pressed : hot ? palette.Hover : null;
        switch (entry.Style)
        {
            case FlatButtonStyle.Primary when !disabled:
                fill = palette.Accent;
                foreground = palette.AccentText;
                break;
            case FlatButtonStyle.CaptionClose when hot || pressed:
                fill = palette.Danger;
                foreground = palette.DangerText;
                break;
        }

        var square = entry.Style == FlatButtonStyle.Caption || entry.Style == FlatButtonStyle.CaptionClose;
        if (fill is { } color) NativePaint.FillRounded(item.hDC, rect, square ? 0 : radius, color, _theme);
        if (entry.Style == FlatButtonStyle.Subtle) NativePaint.FrameRounded(item.hDC, rect, radius, palette.Border, _theme);
        if (disabled) foreground = palette.DisabledText;

        var glyph = entry.Glyph;
        var font = glyph is null
            ? (entry.Style == FlatButtonStyle.Primary ? _fonts.Strong : _fonts.Body)
            : (square || entry.Style == FlatButtonStyle.SmallIcon ? _fonts.SmallIcon : _fonts.Icon);
        var text = glyph ?? NativeMethods.GetWindowText(item.hwndItem);
        NativePaint.Text(item.hDC, text, rect, font, foreground,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE | NativeMethods.DT_END_ELLIPSIS);

        if (focused) NativePaint.FrameRounded(item.hDC, rect, square ? 0 : radius, palette.Accent, _theme);
        return true;
    }

    public void Dispose()
    {
        foreach (var button in _buttons.Keys)
        {
            NativeMethods.RemoveWindowSubclass(button, _subclass, new UIntPtr(0xB7));
        }

        _buttons.Clear();
        _tooltipTools.Clear();
        _hot = IntPtr.Zero;
        if (_tooltip != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_tooltip);
            _tooltip = IntPtr.Zero;
        }
    }

    private void RefreshTooltip()
    {
        if (_tooltip == IntPtr.Zero) return;
        NativeMethods.SetWindowFont(_tooltip, _fonts.Body, false);
        _theme.ApplyToControl(_tooltip);
    }

    private IntPtr OnButtonMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        try
        {
            return OnButtonMessageCore(hWnd, msg, wParam, lParam);
        }
        catch (Exception exception)
        {
            // A managed exception must never cross the native callback boundary.
            Trace.TraceError("The button message {0} failed: {1}", msg, exception);
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr OnButtonMessageCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // The system erases an owner-drawn button with the parent's WM_CTLCOLORBTN brush,
            // which is the light system color unless the window answers that message. Erasing
            // first and painting after is what flashes a light block on dark chrome, so the
            // erase step is suppressed and WM_DRAWITEM paints the whole button.
            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);

            case NativeMethods.WM_CTLCOLORBTN:
                if (_buttons.TryGetValue(hWnd, out var entry) && _theme is { } theme)
                {
                    var backdrop = theme.Palette.Backdrop(entry.Backdrop);
                    NativeMethods.SetBkColor(wParam, backdrop);
                    return theme.Brush(backdrop);
                }

                break;

            case NativeMethods.WM_MOUSEMOVE when _hot != hWnd:
                var previous = _hot;
                _hot = hWnd;
                var track = new NativeMethods.TRACKMOUSEEVENT
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                    dwFlags = NativeMethods.TME_LEAVE,
                    hwndTrack = hWnd,
                };
                NativeMethods.TrackMouseEvent(ref track);
                if (previous != IntPtr.Zero) NativeMethods.InvalidateRect(previous, IntPtr.Zero, false);
                NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                break;

            case NativeMethods.WM_MOUSELEAVE:
                if (_hot == hWnd) _hot = IntPtr.Zero;
                NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                break;

            // Owner-drawn buttons treat a fast second click as a double-click; keep it a click.
            case NativeMethods.WM_LBUTTONDBLCLK:
                return NativeMethods.DefSubclassProc(hWnd, NativeMethods.WM_LBUTTONDOWN, wParam, lParam);
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
