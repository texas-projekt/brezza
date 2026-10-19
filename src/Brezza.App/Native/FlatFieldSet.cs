using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Brezza.App.Native;

/// <summary>
/// Flat, theme-aware rendering for standard <c>EDIT</c> and <c>COMBOBOX</c> controls, so every text
/// input and drop-down in the shell looks and behaves the same: a rounded field, a hairline border
/// that darkens on hover, and an accent underline while the field has keyboard focus.
/// </summary>
/// <remarks>
/// The controls stay real Win32 controls with their own text, selection, keyboard, and
/// notifications. An edit draws its frame in its non-client area (so its parent paints nothing);
/// a combo box is owner-drawn and its closed field is painted here, including the chevron.
/// Combo boxes must be created with <c>CBS_OWNERDRAWFIXED | CBS_HASSTRINGS</c>.
/// <para>
/// The system drop-down list is never shown: it is square, animates, and cannot follow the theme.
/// Every way of opening it (click, F4, Alt+Down, <c>CB_SHOWDROPDOWN</c>) opens the shared
/// <see cref="FlatPopup"/> instead, and a choice is reported to the parent with the usual
/// <c>CBN_SELCHANGE</c>, so callers keep using the standard combo-box messages.
/// </para>
/// </remarks>
internal sealed class FlatFieldSet : IDisposable
{
    private const string GlyphChevron = "\uE70D";
    private const int Padding = 10;
    private const int ChevronWidth = 32;

    private sealed class Field
    {
        public bool Combo;
        public bool Editable;
        public FlatBackdrop Backdrop;
        public IntPtr Edit;
        public IntPtr List;
    }

    private readonly Dictionary<IntPtr, Field> _fields = new();
    private readonly Dictionary<IntPtr, IntPtr> _innerEdits = new();
    private readonly NativeThemeResources _theme;
    private readonly NativeFonts _fonts;
    private readonly Func<uint> _dpi;
    private readonly NativeMethods.SubClassProc _fieldSubclass;
    private readonly NativeMethods.SubClassProc _innerEditSubclass;
    private readonly FlatPopup _popup;
    private IntPtr _hot;

    public FlatFieldSet(NativeThemeResources theme, NativeFonts fonts, Func<uint> dpi)
    {
        _theme = theme;
        _fonts = fonts;
        _dpi = dpi;
        _fieldSubclass = OnFieldMessage;
        _innerEditSubclass = OnInnerEditMessage;
        _popup = new FlatPopup(theme, fonts, dpi);
    }

    /// <summary>The window styles a flat single-line text field is created with.</summary>
    public const uint EditStyle = NativeMethods.WS_CHILD | NativeMethods.ES_AUTOHSCROLL;

    /// <summary>The window styles a flat selection-only drop-down is created with.</summary>
    public const uint ListStyle = NativeMethods.WS_CHILD | NativeMethods.WS_VSCROLL | NativeMethods.CBS_DROPDOWNLIST |
        NativeMethods.CBS_OWNERDRAWFIXED | NativeMethods.CBS_HASSTRINGS;

    /// <summary>The window styles a flat editable drop-down (such as the address field) is created with.</summary>
    public const uint EditableListStyle = NativeMethods.WS_CHILD | NativeMethods.WS_VSCROLL | NativeMethods.WS_CLIPCHILDREN |
        NativeMethods.CBS_DROPDOWN | NativeMethods.CBS_AUTOHSCROLL | NativeMethods.CBS_OWNERDRAWFIXED | NativeMethods.CBS_HASSTRINGS;

    public void RegisterEdit(IntPtr edit, FlatBackdrop backdrop)
    {
        if (edit == IntPtr.Zero) return;
        _fields[edit] = new Field { Backdrop = backdrop };
        NativeMethods.SetWindowSubclass(edit, _fieldSubclass, new UIntPtr(0xF1), IntPtr.Zero);
        RecalculateFrame(edit);
    }

    public void RegisterCombo(IntPtr combo, FlatBackdrop backdrop)
    {
        if (combo == IntPtr.Zero) return;
        var info = new NativeMethods.COMBOBOXINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.COMBOBOXINFO>() };
        NativeMethods.GetComboBoxInfo(combo, ref info);
        var editable = info.hwndItem != IntPtr.Zero && info.hwndItem != combo;
        var field = new Field
        {
            Combo = true,
            Editable = editable,
            Backdrop = backdrop,
            Edit = editable ? info.hwndItem : IntPtr.Zero,
            List = info.hwndList,
        };
        _fields[combo] = field;
        NativeMethods.SetWindowSubclass(combo, _fieldSubclass, new UIntPtr(0xF1), IntPtr.Zero);
        if (field.Edit != IntPtr.Zero)
        {
            _innerEdits[field.Edit] = combo;
            NativeMethods.SetWindowSubclass(field.Edit, _innerEditSubclass, new UIntPtr(0xF2), IntPtr.Zero);
        }
    }

    /// <summary>
    /// Re-applies fonts, item heights, and system dark rendering after a theme or DPI change.
    /// Combo boxes get the given closed-field height.
    /// </summary>
    public void Refresh(int fieldHeight)
    {
        foreach (var pair in _fields)
        {
            var control = pair.Key;
            var field = pair.Value;
            NativeMethods.SetWindowFont(control, _fonts.Body, false);
            if (field.Combo)
            {
                if (field.Edit != IntPtr.Zero) _theme.ApplyToField(field.Edit);
                SetComboHeight(control, fieldHeight);
                PositionInnerEdit(control, field);
                NativeMethods.InvalidateRect(control, IntPtr.Zero, false);
            }
            else
            {
                _theme.ApplyToField(control);
                RecalculateFrame(control);
            }
        }
    }

    /// <summary>Gives the colors of a field's text area; returns false for controls that are not fields.</summary>
    public bool TryGetColors(IntPtr control, IntPtr dc, out IntPtr brush)
    {
        brush = IntPtr.Zero;
        if (!_fields.ContainsKey(control) && !_innerEdits.ContainsKey(control)) return false;
        var palette = _theme.Palette;
        NativeMethods.SetTextColor(dc, palette.Text);
        NativeMethods.SetBkColor(dc, palette.Field);
        brush = _theme.Brush(palette.Field);
        return true;
    }

    /// <summary>Answers <c>WM_MEASUREITEM</c> for owner-drawn combo boxes.</summary>
    public bool TryMeasure(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return false;
        var item = Marshal.PtrToStructure<NativeMethods.MEASUREITEMSTRUCT>(lParam);
        if (item.CtlType != NativeMethods.ODT_COMBOBOX) return false;
        // The closed-field height is corrected by Refresh once the control exists; list rows are never
        // measured for display because the pop-up replaces the system list.
        item.itemHeight = (uint)Scale(24);
        Marshal.StructureToPtr(item, lParam, false);
        return true;
    }

    /// <summary>Paints the closed field of an owner-drawn combo box from <c>WM_DRAWITEM</c>.</summary>
    public bool TryDraw(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return false;
        var item = Marshal.PtrToStructure<NativeMethods.DRAWITEMSTRUCT>(lParam);
        if (item.CtlType != NativeMethods.ODT_COMBOBOX || !_fields.TryGetValue(item.hwndItem, out var field)) return false;

        // The control draws its closed field outside WM_PAINT on focus and selection changes. List
        // rows are never painted here: the system list is replaced by the shared pop-up.
        if ((item.itemState & NativeMethods.ODS_COMBOBOXEDIT) != 0) PaintCombo(item.hwndItem, item.hDC, field);
        return true;
    }

    public void Dispose()
    {
        _popup.Dispose();
        foreach (var control in _fields.Keys) NativeMethods.RemoveWindowSubclass(control, _fieldSubclass, new UIntPtr(0xF1));
        foreach (var edit in _innerEdits.Keys) NativeMethods.RemoveWindowSubclass(edit, _innerEditSubclass, new UIntPtr(0xF2));
        _fields.Clear();
        _innerEdits.Clear();
    }

    // ---------- Subclasses ----------

    private IntPtr OnFieldMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        try
        {
            return OnFieldMessageCore(hWnd, msg, wParam, lParam);
        }
        catch (Exception exception)
        {
            // The field is a real Win32 control; an exception must not escape its subclass.
            Trace.TraceError("The field message {0} failed: {1}", msg, exception);
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr OnFieldMessageCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_fields.TryGetValue(hWnd, out var field)) return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
        switch (msg)
        {
            case NativeMethods.WM_MOUSEMOVE:
                SetHot(hWnd);
                break;

            case NativeMethods.WM_MOUSELEAVE:
                ClearHot(hWnd);
                break;

            case NativeMethods.WM_SETFOCUS:
            case NativeMethods.WM_KILLFOCUS:
            case NativeMethods.WM_ENABLE:
                var focusResult = NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
                Redraw(hWnd, field);
                return focusResult;

            case NativeMethods.WM_CTLCOLOREDIT:
            case NativeMethods.WM_CTLCOLORLISTBOX:
                if (field.Combo && TryGetColors(lParam, wParam, out var brush)) return brush;
                break;
        }

        return field.Combo ? OnComboMessage(hWnd, field, msg, wParam, lParam) : OnEditMessage(hWnd, field, msg, wParam, lParam);
    }

    private IntPtr OnEditMessage(IntPtr hWnd, Field field, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // Same reason as the combo box: the edit fills its own client area in WM_PAINT, so a
            // system-colored erase step in front of it is only a flash.
            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);

            case NativeMethods.WM_NCCALCSIZE:
                // Inset the client area: horizontal padding and a vertically centered text line.
                var rect = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
                var line = LineHeight(hWnd);
                var vertical = Math.Max(1, (rect.Height - line) / 2);
                var horizontal = Scale(Padding);
                rect.left += horizontal;
                rect.right -= horizontal;
                rect.top += vertical;
                rect.bottom = Math.Max(rect.top, rect.bottom - vertical);
                Marshal.StructureToPtr(rect, lParam, false);
                return IntPtr.Zero;

            case NativeMethods.WM_NCPAINT:
                PaintEditFrame(hWnd, field);
                return IntPtr.Zero;

            case NativeMethods.WM_NCHITTEST:
                // Clicks on the padding place the caret like clicks on the text do.
                return new IntPtr(NativeMethods.HTCLIENT);

            case NativeMethods.WM_SETFONT:
                var fontResult = NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
                RecalculateFrame(hWnd);
                return fontResult;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr OnComboMessage(IntPtr hWnd, Field field, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_LBUTTONDOWN:
            case NativeMethods.WM_LBUTTONDBLCLK:
                // An editable combo's text area is its edit child, so a click here is on the chevron.
                NativeMethods.SetFocus(field.Edit != IntPtr.Zero ? field.Edit : hWnd);
                TogglePopup(hWnd, field);
                return IntPtr.Zero;

            case NativeMethods.WM_KEYDOWN when IsDropDownKey(wParam, alt: false):
            case NativeMethods.WM_SYSKEYDOWN when IsDropDownKey(wParam, alt: true):
                TogglePopup(hWnd, field);
                return IntPtr.Zero;

            case NativeMethods.CB_SHOWDROPDOWN:
                if (wParam != IntPtr.Zero) OpenPopup(hWnd, field);
                else if (_popup.Anchor == hWnd) _popup.Close();
                return new IntPtr(1);

            case NativeMethods.CB_GETDROPPEDSTATE:
                return new IntPtr(_popup.Anchor == hWnd ? 1 : 0);

            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);

            case NativeMethods.WM_PAINT:
                if (wParam != IntPtr.Zero)
                {
                    PaintCombo(hWnd, wParam, field);
                    return IntPtr.Zero;
                }

                var paint = new NativeMethods.PAINTSTRUCT();
                var dc = NativeMethods.BeginPaint(hWnd, ref paint);
                try
                {
                    PaintCombo(hWnd, dc, field);
                }
                finally
                {
                    NativeMethods.EndPaint(hWnd, ref paint);
                }

                return IntPtr.Zero;

            case NativeMethods.WM_SIZE:
            case NativeMethods.WM_SETFONT:
            case NativeMethods.CB_SETITEMHEIGHT:
                // The control lays out its edit child on these; move it inside the painted field.
                var result = NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
                PositionInnerEdit(hWnd, field);
                return result;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr OnInnerEditMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        try
        {
            return OnInnerEditMessageCore(hWnd, msg, wParam, lParam);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The inner edit message {0} failed: {1}", msg, exception);
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr OnInnerEditMessageCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_innerEdits.TryGetValue(hWnd, out var combo))
        {
            switch (msg)
            {
                case NativeMethods.WM_SETFOCUS:
                case NativeMethods.WM_KILLFOCUS:
                    var result = NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
                    NativeMethods.InvalidateRect(combo, IntPtr.Zero, false);
                    return result;

                case NativeMethods.WM_MOUSEMOVE:
                    SetHot(combo, hWnd);
                    break;

                case NativeMethods.WM_MOUSELEAVE:
                    // Moving from the edit onto the combo's chevron keeps the field hot.
                    if (!CursorOver(combo)) ClearHot(combo);
                    break;

                case NativeMethods.WM_KEYDOWN when IsDropDownKey(wParam, alt: false):
                case NativeMethods.WM_SYSKEYDOWN when IsDropDownKey(wParam, alt: true):
                    if (_fields.TryGetValue(combo, out var owner)) TogglePopup(combo, owner);
                    return IntPtr.Zero;
            }
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    // ---------- Pop-up ----------

    private static bool IsDropDownKey(IntPtr wParam, bool alt)
    {
        var key = NativeMethods.GetLoWord(wParam);
        return alt ? key == NativeMethods.VK_DOWN || key == NativeMethods.VK_UP : key == NativeMethods.VK_F4;
    }

    private void TogglePopup(IntPtr combo, Field field)
    {
        if (_popup.Anchor == combo) _popup.Close();
        else OpenPopup(combo, field);
    }

    private void OpenPopup(IntPtr combo, Field field)
    {
        if (!NativeMethods.IsWindowEnabled(combo)) return;
        var parent = NativeMethods.GetParent(combo);
        var id = NativeMethods.GetDlgCtrlID(combo);

        // The parent fills lazy lists (the address suggestions) on CBN_DROPDOWN, as with the system list.
        NativeMethods.SendMessage(parent, NativeMethods.WM_COMMAND,
            new IntPtr(NativeMethods.MakeLong(id, NativeMethods.CBN_DROPDOWN)), combo);

        var count = (int)NativeMethods.SendMessage(combo, NativeMethods.CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return;
        var selected = field.Editable
            ? -1
            : (int)NativeMethods.SendMessage(combo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        var items = new List<FlatPopupItem>(count);
        for (var index = 0; index < count; index++)
        {
            items.Add(new FlatPopupItem(NativeMethods.GetComboItemText(combo, index), selected: index == selected));
        }

        NativeMethods.GetWindowRect(combo, out var bounds);
        NativeMethods.GetClientRect(combo, out var client);
        bounds.bottom = bounds.top + client.Height;
        _popup.Show(parent, combo, bounds, items,
            chosen: index =>
            {
                NativeMethods.SendMessage(combo, NativeMethods.CB_SETCURSEL, new IntPtr(index), IntPtr.Zero);
                NativeMethods.InvalidateRect(combo, IntPtr.Zero, false);
                // A programmatic selection does not notify, so report the user's choice like the system list.
                NativeMethods.SendMessage(parent, NativeMethods.WM_COMMAND,
                    new IntPtr(NativeMethods.MakeLong(id, NativeMethods.CBN_SELCHANGE)), combo);
            },
            closed: () => NativeMethods.InvalidateRect(combo, IntPtr.Zero, false));
        NativeMethods.InvalidateRect(combo, IntPtr.Zero, false);
    }

    // ---------- Painting ----------

    private void PaintCombo(IntPtr combo, IntPtr target, Field field)
    {
        NativeMethods.GetClientRect(combo, out var client);
        using var buffer = new NativeBufferedPaint(target, client);
        var dc = buffer.Dc;
        using var paint = NativePaintScope.Begin(dc, "combo");
        var palette = _theme.Palette;
        var enabled = NativeMethods.IsWindowEnabled(combo);
        PaintField(dc, client, field.Backdrop, IsFocused(combo, field) || _popup.Anchor == combo, combo == _hot && enabled);

        var chevron = NativePaint.Rect(client.right - Scale(ChevronWidth), client.top, Scale(ChevronWidth), client.Height);
        NativePaint.Text(dc, GlyphChevron, chevron, _fonts.SmallIcon, enabled ? palette.SecondaryText : palette.DisabledText,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);

        if (field.Editable) return;
        var selected = (int)NativeMethods.SendMessage(combo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        if (selected < 0) return;
        var text = client;
        text.left += Scale(Padding);
        text.right = chevron.left;
        NativePaint.Text(dc, NativeMethods.GetComboItemText(combo, selected), text, _fonts.Body,
            enabled ? palette.Text : palette.DisabledText,
            NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE | NativeMethods.DT_END_ELLIPSIS);
    }

    private void PaintEditFrame(IntPtr edit, Field field)
    {
        var dc = NativeMethods.GetWindowDC(edit);
        if (dc == IntPtr.Zero) return;
        try
        {
            NativeMethods.GetWindowRect(edit, out var window);
            NativeMethods.GetClientRect(edit, out var client);
            var origin = new NativeMethods.POINT();
            NativeMethods.ClientToScreen(edit, ref origin);
            var left = origin.x - window.left;
            var top = origin.y - window.top;

            // The edit paints its own text area; the frame covers only the non-client ring.
            NativeMethods.ExcludeClipRect(dc, left, top, left + client.Width, top + client.Height);
            var bounds = NativePaint.Rect(0, 0, window.Width, window.Height);
            using var paint = NativePaintScope.Begin(dc, "edit frame");
            PaintField(dc, bounds, field.Backdrop, NativeMethods.GetFocus() == edit, edit == _hot);
        }
        finally
        {
            NativeMethods.ReleaseDC(edit, dc);
        }
    }

    /// <summary>The shared field look: rounded fill, hairline border, and an accent underline when focused.</summary>
    private void PaintField(IntPtr dc, NativeMethods.RECT bounds, FlatBackdrop backdrop, bool focused, bool hot)
    {
        var palette = _theme.Palette;
        var backdropColor = palette.Backdrop(backdrop);
        var radius = Scale(NativeMetrics.RadiusControl);
        NativePaint.Fill(dc, bounds, backdropColor, _theme);
        NativePaint.FillRounded(dc, bounds, radius, palette.Field, _theme);
        NativePaint.FrameRounded(dc, bounds, radius, hot && !focused ? palette.BorderHover : palette.Border, _theme);
        if (!focused) return;

        var underline = Math.Max(2, Scale(NativeMetrics.FocusIndicatorThickness));
        NativePaint.Fill(dc, NativePaint.Rect(bounds.left + radius / 2, bounds.bottom - underline,
            bounds.Width - radius, underline), palette.Accent, _theme);
    }

    // ---------- Helpers ----------

    private void PositionInnerEdit(IntPtr combo, Field field)
    {
        if (field.Edit == IntPtr.Zero) return;
        NativeMethods.GetClientRect(combo, out var client);
        var line = LineHeight(field.Edit);
        var left = Scale(Padding);
        var width = Math.Max(0, client.Width - left - Scale(ChevronWidth));
        NativeMethods.MoveWindow(field.Edit, left, Math.Max(0, (client.Height - line) / 2), width, line, true);
    }

    /// <summary>Adjusts the closed-field item height until the combo box window is the requested height.</summary>
    private static void SetComboHeight(IntPtr combo, int height)
    {
        NativeMethods.GetWindowRect(combo, out var rect);
        var item = (int)NativeMethods.SendMessage(combo, NativeMethods.CB_GETITEMHEIGHT, new IntPtr(-1), IntPtr.Zero);
        var target = Math.Max(1, item + height - rect.Height);
        NativeMethods.SendMessage(combo, NativeMethods.CB_SETITEMHEIGHT, new IntPtr(-1), new IntPtr(target));
    }

    private static int LineHeight(IntPtr control)
    {
        var font = NativeMethods.SendMessage(control, NativeMethods.WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
        return Math.Max(1, NativeFonts.Measure(control, font, "Ag").y);
    }

    private static void RecalculateFrame(IntPtr control) =>
        NativeMethods.SetWindowPos(control, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

    private static void Redraw(IntPtr control, Field field)
    {
        if (field.Combo) NativeMethods.InvalidateRect(control, IntPtr.Zero, false);
        else NativeMethods.RedrawWindow(control, IntPtr.Zero, IntPtr.Zero, NativeMethods.RDW_FRAME | NativeMethods.RDW_INVALIDATE);
    }

    private static bool IsFocused(IntPtr combo, Field field)
    {
        var focus = NativeMethods.GetFocus();
        return focus == combo || (field.Edit != IntPtr.Zero && focus == field.Edit);
    }

    private static bool CursorOver(IntPtr control)
    {
        NativeMethods.GetCursorPos(out var point);
        NativeMethods.GetWindowRect(control, out var rect);
        return point.x >= rect.left && point.x < rect.right && point.y >= rect.top && point.y < rect.bottom;
    }

    private void SetHot(IntPtr control, IntPtr? tracked = null)
    {
        Track(tracked ?? control);
        if (_hot == control) return;
        var previous = _hot;
        _hot = control;
        if (previous != IntPtr.Zero && _fields.TryGetValue(previous, out var old)) Redraw(previous, old);
        if (_fields.TryGetValue(control, out var field)) Redraw(control, field);
    }

    private void ClearHot(IntPtr control)
    {
        if (_hot != control) return;
        _hot = IntPtr.Zero;
        if (_fields.TryGetValue(control, out var field)) Redraw(control, field);
    }

    private static void Track(IntPtr control)
    {
        var track = new NativeMethods.TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
            dwFlags = NativeMethods.TME_LEAVE,
            hwndTrack = control,
        };
        NativeMethods.TrackMouseEvent(ref track);
    }

    private int Scale(int value) => NativeMethods.Scale(value, _dpi());
}
