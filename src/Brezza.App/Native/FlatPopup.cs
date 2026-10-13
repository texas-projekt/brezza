using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Brezza.App.Native;

/// <summary>One row of a <see cref="FlatPopup"/>.</summary>
internal sealed class FlatPopupItem
{
    public FlatPopupItem(string text, string? shortcut = null, bool separatorBefore = false, bool selected = false)
    {
        Text = text;
        Shortcut = shortcut;
        SeparatorBefore = separatorBefore;
        Selected = selected;
    }

    public string Text { get; }

    /// <summary>Right-aligned hint such as a keyboard shortcut.</summary>
    public string? Shortcut { get; }

    public bool SeparatorBefore { get; }

    /// <summary>Marks the current value of a drop-down.</summary>
    public bool Selected { get; }
}

/// <summary>
/// The one pop-up surface of the shell: drop-down lists and the toolbar menu both open here, so they
/// share the same look (rounded corners and a soft shadow from the system, flat rows, accent marker)
/// and the same behavior (appears at once without animation, keyboard navigation, closes on outside
/// click, Escape, or focus loss).
/// </summary>
/// <remarks>
/// The pop-up never takes activation: the owner window stays active and keeps keyboard focus, and the
/// owner's message loop forwards navigation keys through <see cref="TryHandleKey"/>.
/// </remarks>
internal sealed class FlatPopup : IDisposable
{
    public const string WindowClassName = "BrezzaFlatPopup";

    private const int RowHeight = 32;
    private const int SeparatorHeight = 9;
    private const int Inset = 4;
    private const int TextPadding = 12;
    private const int MaximumVisibleRows = 10;

    private readonly NativeThemeResources _theme;
    private readonly NativeFonts _fonts;
    private readonly Func<uint> _dpi;
    private readonly NativeMethods.WndProc _wndProc;
    private readonly List<FlatPopupItem> _items = new();
    private IntPtr _hwnd;
    private IntPtr _owner;
    private Action<int>? _chosen;
    private Action? _closed;
    private int _hot = -1;
    private int _scroll;

    public FlatPopup(NativeThemeResources theme, NativeFonts fonts, Func<uint> dpi)
    {
        _theme = theme;
        _fonts = fonts;
        _dpi = dpi;
        _wndProc = OnMessage;
    }

    public bool IsOpen => _hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(_hwnd);

    /// <summary>The pop-up that is open right now, if any; the message loop routes navigation keys to it.</summary>
    public static FlatPopup? Current { get; private set; }

    /// <summary>The control the open pop-up belongs to, so a second click on it can close it.</summary>
    public IntPtr Anchor { get; private set; }

    /// <summary>
    /// Opens the pop-up under (or, without room, above) an anchor rectangle in screen coordinates.
    /// <paramref name="chosen"/> receives the index of the row the user picks.
    /// </summary>
    public void Show(IntPtr owner, IntPtr anchor, NativeMethods.RECT anchorRect, IReadOnlyList<FlatPopupItem> items,
        Action<int> chosen, Action? closed = null, bool alignRight = false, int minimumWidth = 0)
    {
        Current?.Close();
        Close();
        if (items.Count == 0) return;
        _items.Clear();
        _items.AddRange(items);
        _owner = owner;
        Anchor = anchor;
        _chosen = chosen;
        _closed = closed;
        _scroll = 0;
        _hot = _items.FindIndex(item => item.Selected);
        EnsureWindow(owner);

        var width = Math.Max(Math.Max(minimumWidth, anchorRect.Width), MeasureWidth());
        var height = ContentHeight(VisibleCount()) + Scale(Inset) * 2;
        var work = WorkArea(anchorRect);
        var x = alignRight ? anchorRect.right - width : anchorRect.left;
        x = Math.Max(work.left, Math.Min(x, work.right - width));
        var gap = Scale(4);
        var y = anchorRect.bottom + gap;
        if (y + height > work.bottom && anchorRect.top - gap - height >= work.top) y = anchorRect.top - gap - height;
        EnsureVisible(_hot);

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, x, y, width, Math.Min(height, work.Height),
            NativeMethods.SWP_NOACTIVATE);
        _theme.ApplyToWindowFrame(_hwnd);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        Current = this;

        // Capture routes clicks outside the pop-up here, which is how an outside click dismisses it.
        NativeMethods.SetCapture(_hwnd);
    }

    public void Close()
    {
        if (!IsOpen) return;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        if (ReferenceEquals(Current, this)) Current = null;
        if (NativeMethods.GetCapture() == _hwnd) NativeMethods.ReleaseCapture();
        Anchor = IntPtr.Zero;
        var closed = _closed;
        _closed = null;
        _chosen = null;
        closed?.Invoke();
    }

    /// <summary>Handles navigation keys while open; returns true when the key was consumed.</summary>
    public bool TryHandleKey(int virtualKey)
    {
        if (!IsOpen) return false;
        switch (virtualKey)
        {
            case NativeMethods.VK_ESCAPE:
                Close();
                return true;
            case NativeMethods.VK_TAB:
                // Tab closes the pop-up and still moves focus to the next control.
                Close();
                return false;
            case NativeMethods.VK_UP:
                MoveHot(-1);
                return true;
            case NativeMethods.VK_DOWN:
                MoveHot(1);
                return true;
            case NativeMethods.VK_HOME:
                SetHot(0);
                return true;
            case NativeMethods.VK_END:
                SetHot(_items.Count - 1);
                return true;
            case NativeMethods.VK_RETURN:
                if (_hot >= 0) Choose(_hot);
                else Close();
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        Close();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    // ---------- Window ----------

    // A window class is registered once per process, with one window procedure. Several pop-ups exist
    // at once (address suggestions, the toolbar menu, each settings drop-down), so the class procedure
    // is static and routes every message to the instance that owns the window. Registering a
    // per-instance procedure would hand every later pop-up's messages to the first instance: wrong
    // rows painted, clicks acted on by the wrong owner, and mouse capture that is never released.
    private static readonly NativeMethods.WndProc ClassProcedure = RouteMessage;
    private static readonly Dictionary<IntPtr, FlatPopup> Instances = new();
    private static FlatPopup? _creating;
    private static bool _registered;

    private static IntPtr RouteMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Instances.TryGetValue(hWnd, out var popup) && _creating is { } creating)
        {
            // Messages sent during CreateWindowEx arrive before the handle is known to the caller.
            popup = creating;
            Instances[hWnd] = popup;
        }

        if (msg == 0x0082) Instances.Remove(hWnd); // WM_NCDESTROY
        return popup is null
            ? NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam)
            : popup._wndProc(hWnd, msg, wParam, lParam);
    }

    private void EnsureWindow(IntPtr owner)
    {
        // The window is owned by the window the pop-up opens from; when that owner is destroyed
        // Windows destroys the pop-up with it, so a stale handle is recreated here.
        if (_hwnd != IntPtr.Zero && Instances.ContainsKey(_hwnd) && NativeMethods.GetParent(_hwnd) == owner) return;
        if (_hwnd != IntPtr.Zero && Instances.ContainsKey(_hwnd)) NativeMethods.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;

        if (!_registered)
        {
            var wndClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                style = NativeMethods.CS_DROPSHADOW,
                lpfnWndProc = ClassProcedure,
                hInstance = NativeMethods.GetModuleHandle(null),
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(32512)),
                lpszClassName = WindowClassName,
            };
            if (NativeMethods.RegisterClassEx(ref wndClass) == 0 &&
                Marshal.GetLastWin32Error() != NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new InvalidOperationException(
                    "The pop-up window class could not be registered (error " + Marshal.GetLastWin32Error() + ").");
            }

            _registered = true;
        }

        _creating = this;
        try
        {
            // Not topmost: an owned window always stays directly above its owner, so the pop-up shares
            // the owner's place in the z-order. A topmost pop-up would float over other apps that
            // cover the browser.
            _hwnd = NativeMethods.CreateWindowEx(
                NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
                WindowClassName, null, NativeMethods.WS_POPUP,
                0, 0, 0, 0, owner, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        }
        finally
        {
            _creating = null;
        }

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("The pop-up could not be created (error " + Marshal.GetLastWin32Error() + ").");
        }

        Instances[_hwnd] = this;

        // Windows 11 rounds and anti-aliases the corners itself; older systems keep square corners.
        var corner = NativeMethods.DWMWCP_ROUNDSMALL;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // Show and hide at once: no DWM fade or slide, matching every other surface of the shell.
        var noTransitions = 1;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref noTransitions, sizeof(int));
    }

    private IntPtr OnMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case NativeMethods.WM_MOUSEACTIVATE:
                    return new IntPtr(NativeMethods.MA_NOACTIVATE);

                case NativeMethods.WM_ERASEBKGND:
                    return new IntPtr(1);

                case NativeMethods.WM_PAINT:
                    Paint(hWnd);
                    return IntPtr.Zero;

                case NativeMethods.WM_MOUSEMOVE:
                    var hover = HitTest(lParam);
                    if (hover >= 0) SetHot(hover, scroll: false);
                    return IntPtr.Zero;

                case NativeMethods.WM_LBUTTONDOWN:
                case NativeMethods.WM_RBUTTONDOWN:
                case NativeMethods.WM_MBUTTONDOWN:
                    if (!Contains(lParam))
                    {
                        // An outside click only dismisses; it never reaches the control under it, so
                        // clicking the anchor again closes the pop-up instead of reopening it.
                        Close();
                        return IntPtr.Zero;
                    }

                    return IntPtr.Zero;

                case NativeMethods.WM_LBUTTONUP:
                    var row = HitTest(lParam);
                    if (row >= 0) Choose(row);
                    return IntPtr.Zero;

                case NativeMethods.WM_MOUSEWHEEL:
                    Scroll(unchecked((short)NativeMethods.GetHiWord(wParam)) > 0 ? -1 : 1);
                    return IntPtr.Zero;

                case NativeMethods.WM_CAPTURECHANGED:
                    // Another window took the mouse (a system dialog, Alt+Tab): the pop-up must not linger.
                    if (lParam != hWnd) Close();
                    return IntPtr.Zero;
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("The pop-up message {0} failed: {1}", msg, exception);
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Paint(IntPtr hWnd)
    {
        var paint = new NativeMethods.PAINTSTRUCT();
        var target = NativeMethods.BeginPaint(hWnd, ref paint);
        try
        {
            NativeMethods.GetClientRect(hWnd, out var client);
            using var buffer = new NativeBufferedPaint(target, client);
            var dc = buffer.Dc;
            using var scope = NativePaintScope.Begin(dc, "popup");
            var palette = _theme.Palette;
            NativePaint.Fill(dc, client, palette.Surface, _theme);

            var inset = Scale(Inset);
            var y = inset;
            var last = Math.Min(_items.Count, _scroll + VisibleCount());
            for (var index = _scroll; index < last; index++)
            {
                var item = _items[index];
                if (item.SeparatorBefore && index > _scroll)
                {
                    NativePaint.Fill(dc, NativePaint.Rect(inset, y + Scale(SeparatorHeight) / 2, client.Width - inset * 2, 1),
                        palette.Border, _theme);
                    y += Scale(SeparatorHeight);
                }

                var row = NativePaint.Rect(inset, y, client.Width - inset * 2, Scale(RowHeight));
                if (index == _hot) NativePaint.FillRounded(dc, row, Scale(4), palette.Hover, _theme);
                if (item.Selected)
                {
                    // The same short accent bar Windows 11 uses to mark the current value of a list.
                    var bar = Scale(3);
                    var barHeight = Scale(16);
                    NativePaint.FillRounded(dc, NativePaint.Rect(row.left, row.top + (row.Height - barHeight) / 2, bar, barHeight),
                        bar / 2, palette.Accent, _theme);
                }

                var text = row;
                text.left += Scale(TextPadding);
                text.right -= Scale(TextPadding);
                const uint line = NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER | NativeMethods.DT_END_ELLIPSIS;
                if (!string.IsNullOrEmpty(item.Shortcut))
                {
                    NativePaint.Text(dc, item.Shortcut!, text, _fonts.Body, palette.SecondaryText, line | 0x0002 /* DT_RIGHT */);
                    text.right -= NativeFonts.Measure(hWnd, _fonts.Body, item.Shortcut!).x + Scale(24);
                }

                NativePaint.Text(dc, item.Text, text, _fonts.Body, palette.Text, line | NativeMethods.DT_LEFT);
                y += Scale(RowHeight);
            }
        }
        finally
        {
            NativeMethods.EndPaint(hWnd, ref paint);
        }
    }

    // ---------- Rows ----------

    private void Choose(int index)
    {
        var chosen = _chosen;
        Close();
        chosen?.Invoke(index);
    }

    private void MoveHot(int step)
    {
        if (_items.Count == 0) return;
        var next = _hot < 0 ? (step > 0 ? 0 : _items.Count - 1) : _hot + step;
        SetHot(Math.Max(0, Math.Min(_items.Count - 1, next)));
    }

    private void SetHot(int index, bool scroll = true)
    {
        if (index == _hot || index < 0 || index >= _items.Count) return;
        _hot = index;
        if (scroll) EnsureVisible(index);
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void Scroll(int step)
    {
        var maximum = Math.Max(0, _items.Count - VisibleCount());
        var next = Math.Max(0, Math.Min(maximum, _scroll + step));
        if (next == _scroll) return;
        _scroll = next;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void EnsureVisible(int index)
    {
        if (index < 0) return;
        if (index < _scroll) _scroll = index;
        else if (index >= _scroll + VisibleCount()) _scroll = index - VisibleCount() + 1;
    }

    private int HitTest(IntPtr lParam)
    {
        var y = unchecked((short)NativeMethods.GetHiWord(lParam));
        var x = unchecked((short)NativeMethods.GetLoWord(lParam));
        NativeMethods.GetClientRect(_hwnd, out var client);
        if (x < 0 || x >= client.Width) return -1;
        var top = Scale(Inset);
        var last = Math.Min(_items.Count, _scroll + VisibleCount());
        for (var index = _scroll; index < last; index++)
        {
            if (_items[index].SeparatorBefore && index > _scroll) top += Scale(SeparatorHeight);
            if (y >= top && y < top + Scale(RowHeight)) return index;
            top += Scale(RowHeight);
        }

        return -1;
    }

    private bool Contains(IntPtr lParam)
    {
        var x = unchecked((short)NativeMethods.GetLoWord(lParam));
        var y = unchecked((short)NativeMethods.GetHiWord(lParam));
        NativeMethods.GetClientRect(_hwnd, out var client);
        return x >= 0 && y >= 0 && x < client.Width && y < client.Height;
    }

    // ---------- Metrics ----------

    private int VisibleCount() => Math.Min(_items.Count, MaximumVisibleRows);

    private int ContentHeight(int rows)
    {
        var height = rows * Scale(RowHeight);
        for (var index = 1; index < rows; index++)
        {
            if (_items[index].SeparatorBefore) height += Scale(SeparatorHeight);
        }

        return height;
    }

    private int MeasureWidth()
    {
        var widest = 0;
        foreach (var item in _items)
        {
            var width = NativeFonts.Measure(_owner, _fonts.Body, item.Text).x;
            if (!string.IsNullOrEmpty(item.Shortcut)) width += NativeFonts.Measure(_owner, _fonts.Body, item.Shortcut!).x + Scale(24);
            widest = Math.Max(widest, width);
        }

        return widest + Scale(TextPadding + Inset) * 2;
    }

    private static NativeMethods.RECT WorkArea(NativeMethods.RECT anchor)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var monitor = NativeMethods.MonitorFromRect(ref anchor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return NativeMethods.GetMonitorInfo(monitor, ref info) ? info.rcWork : anchor;
    }

    private int Scale(int value) => NativeMethods.Scale(value, _dpi());
}
