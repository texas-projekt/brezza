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

/// <summary>The tab strip: sizing, scrolling, the close target, painting, and tab labels.</summary>
internal sealed partial class MainWindow
{
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
    /// theme, and the strip is sized from where the control's own items end so they never overflow,
    /// so they are never needed; any scroll offset left from an earlier overflow is reset first so
    /// the first tab is at the left edge again. Kept as a safety net: a stray arrow would paint over
    /// the themed strip.
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

    /// <summary>Moves the strip by the current offset and keeps the close target with it.</summary>
    private void ApplyTabScroll(bool repositionClose = true)
    {
        // One move, one clip, one repaint. The strip is moved without redrawing and the region is set
        // without redrawing, so nothing is painted between the two: the strip is never seen at the
        // new offset under the old region, and a scroll costs the strip a single repaint instead of a
        // move-repaint, a region-repaint, and four repaints of the controls beside it that a scroll
        // does not change.
        MoveTabStrip(_tabStripWidth, _tabScrollOffset);
        ClipTabStripToBand(repaintNeighbours: false);
        NativeMethods.InvalidateRect(_tabStrip, IntPtr.Zero, false);
        if (repositionClose) PositionTabClose();
    }

    /// <summary>
    /// Clips the strip's window to the band it shows. The window is as wide as its items so the tab
    /// control never scrolls them itself and never shows its system scroll arrows, but that width
    /// runs past the new-tab button and the caption buttons, which sit below the strip in the
    /// z-order: a strip scrolled away from its end would paint over them and erase them. The region
    /// follows the scroll, so the band is always the visible part, and it moves the hit-testing with
    /// it.
    /// </summary>
    /// <param name="repaintNeighbours">
    /// Set when the band itself changed size, which is the only time the controls beside it can look
    /// different. A scroll leaves the band exactly where it was on screen, so it repaints nothing
    /// but the strip.
    /// </param>
    private void ClipTabStripToBand(bool repaintNeighbours)
    {
        if (_tabStrip == IntPtr.Zero) return;
        var left = Math.Max(0, Math.Min(_tabScrollOffset, _tabStripWidth));
        var right = Math.Max(left, Math.Min(_tabScrollOffset + _tabVisibleWidth, _tabStripWidth));
        // The system owns the region handle once it is set, so a fresh one is built each time. The
        // redraw flag is left off: the caller invalidates once, after the strip is in its final
        // place, so the strip is never painted at the new offset under the old region.
        var region = NativeMethods.CreateRectRgn(left, 0, right, Scale(CaptionHeight - TabTop));
        if (NativeMethods.SetWindowRgn(_tabStrip, region, false) == 0)
        {
            // SetWindowRgn only takes ownership of the region when it succeeds.
            NativeMethods.DeleteObject(region);
        }

        if (!repaintNeighbours) return;
        NativeMethods.InvalidateRect(_newTabButton, IntPtr.Zero, false);
        NativeMethods.InvalidateRect(_titleMinimize, IntPtr.Zero, false);
        NativeMethods.InvalidateRect(_titleMaximize, IntPtr.Zero, false);
        NativeMethods.InvalidateRect(_titleClose, IntPtr.Zero, false);
    }

    /// <summary>
    /// Scrolls the strip the least that brings a tab fully into view, as selecting it does, and keeps
    /// the active tab's close button with it.
    /// </summary>
    private void EnsureTabVisible(int index)
    {
        ScrollTabIntoView(index);
        PositionTabClose();
    }

    /// <summary>
    /// Moves the strip the least that brings a tab fully into view and leaves the close button where
    /// it is. A caller that is about to move the selection uses this, scrolls first, and positions
    /// the button once afterwards: the tab control repaints on its own when the selection changes, so
    /// scrolling before that change makes the control's repaint and the strip's repaint at the new
    /// offset the same paint. Scrolling after it paints the row twice per step — once still at the old
    /// offset — which reads as a flicker.
    /// </summary>
    private void ScrollTabIntoView(int index)
    {
        if (!_tabStripScrollable || index < 0 || index >= _tabMirror.Count) return;

        var tab = TabRect(index);
        // The strip window moves, so a tab's screen position is its own rect shifted by the offset.
        var tabLeft = Scale(NativeMetrics.SpaceSmall) - _tabScrollOffset + tab.left;
        var tabRight = Scale(NativeMetrics.SpaceSmall) - _tabScrollOffset + tab.right;
        var rightEdge = Scale(NativeMetrics.SpaceSmall) + _tabVisibleWidth;
        var margin = Scale(8);
        if (tabLeft < margin)
        {
            _tabScrollOffset = Math.Max(0, _tabScrollOffset - (margin - tabLeft));
        }
        else if (tabRight > rightEdge - margin)
        {
            _tabScrollOffset = Math.Min(_tabMaxScroll, _tabScrollOffset + (tabRight - (rightEdge - margin)));
        }
        else
        {
            return;
        }

        ApplyTabScroll(repositionClose: false);
    }

    /// <summary>
    /// Places the close button inside the right edge of the active tab. Tabs share the strip when
    /// many are open, so a tab can end up narrower than the button: the button then shrinks and
    /// centres on its own tab instead of covering its neighbours, and it is never hidden. A close
    /// target that disappears as tabs multiply is a tab the user cannot close.
    /// </summary>
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
        var origin = new NativeMethods.POINT { x = strip.left, y = strip.top };
        NativeMethods.ScreenToClient(_hwnd, ref origin);
        var margin = Scale(TabCloseMargin);
        // A narrow tab shrinks the button rather than losing it, down to a size the glyph fills.
        var size = Math.Min(Scale(TabCloseSize), Math.Max(Scale(14), tab.Width - margin * 2));
        var lowest = tab.left + margin;
        var highest = tab.right - size - margin;
        // Too narrow to sit inside with margins: centre the button on the tab it closes.
        var left = highest <= lowest
            ? tab.left + (tab.Width - size) / 2
            : Math.Min(Math.Max(tab.right - size - Scale(TabCloseInset), lowest), highest);
        var buttonLeft = origin.x + left;
        // A strip that scrolls is wider than the space it shows, so the active tab can move out of
        // sight; its close button goes with it instead of floating over the other caption buttons.
        var bandRight = origin.x + _tabVisibleWidth + _tabScrollOffset;
        var visible = buttonLeft >= 0 && buttonLeft + size <= bandRight;
        NativeMethods.MoveWindow(_closeActiveTabButton,
            buttonLeft, origin.y + (strip.Height - size) / 2, size, size, true);
        ShowControls(visible, _closeActiveTabButton);
    }

    /// <summary>
    /// The narrowest a tab may be: its leading icon, the room its label needs to ellipsize, the
    /// active tab's close button, and the margins both keep. A tab at the minimum width therefore
    /// still shows its favicon and its close target instead of clipping either, and its label
    /// narrows gradually instead of being dropped at a threshold.
    /// </summary>
    public int MinimumTabWidth
    {
        get
        {
            // What DrawText needs to draw the ellipsis a too-long label ends with.
            var ellipsis = Math.Max(Scale(8), NativeFonts.Measure(_hwnd, _fonts.Body, "…").x);
            return Scale(NativeMetrics.SpaceMedium) + Scale(TabIconSize) + Scale(NativeMetrics.SpaceXSmall) +
                ellipsis + Scale(TabCloseSize) + Scale(TabCloseInset) + Scale(TabCloseMargin);
        }
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
            var radius = Scale(NativeMetrics.RadiusTab);
            var padding = Scale(NativeMetrics.SpaceMedium);
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
                    NativePaint.FillRounded(dc, body, Scale(NativeMetrics.RadiusTabHover), palette.Hover, _theme);
                }

                var neighborEmphasized = index + 1 == selected || index + 1 == _hotTab;
                if (!active && !hot && !neighborEmphasized && index + 1 < _tabMirror.Count)
                {
                    var divider = Scale(16);
                    NativePaint.Fill(dc, NativePaint.Rect(rect.right - 1, (client.Height - divider) / 2, 1, divider), palette.Border, _theme);
                }

                // An inactive tab shows its own close glyph while hovered; the active tab has the
                // close button. Either way the label leaves room for it.
                var showsClose = active || hot;
                var textRect = rect;
                textRect.left += padding;
                textRect.right -= showsClose ? Scale(32) : padding;

                if (hot)
                {
                    var glyphRect = TabCloseGlyphRect(item, client.Height);
                    if (_hotTabClose) NativePaint.FillRounded(dc, glyphRect, Scale(NativeMetrics.RadiusControl), palette.Pressed, _theme);
                    NativePaint.Text(dc, GlyphClose, glyphRect, _fonts.SmallIcon, palette.Text,
                        NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
                }

                // A tab playing sound says so at the end of its label, where a user looks for it.
                if (_tabMirror[index].Audio)
                {
                    var audioWidth = Scale(TabIconSize) + Scale(NativeMetrics.SpaceXSmall);
                    var audioRect = NativePaint.Rect(textRect.right - Scale(TabIconSize), 0, Scale(TabIconSize), client.Height);
                    NativePaint.Text(dc, GlyphAudio, audioRect, _fonts.SmallIcon, active ? palette.Text : palette.SecondaryText,
                        NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
                    textRect.right -= audioWidth;
                }

                var privateTab = _tabMirror[index].Private;
                var label = _tabMirror[index].Text;

                // Draws the tab's leading icon from x and returns where its label may start. A
                // private tab leads with its private marker alone: the page's favicon and the
                // built-in page icon would repeat what the marker already says. Every other tab
                // leads with its favicon, or the built-in page icon when the page has none.
                int DrawLead(int x)
                {
                    if (privateTab)
                    {
                        NativePaint.Text(dc, GlyphPrivate,
                            NativePaint.Rect(x, 0, Scale(TabPrivateMarkerWidth), client.Height),
                            _fonts.SmallIcon, palette.Accent,
                            NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
                        return x + Scale(TabPrivateMarkerWidth);
                    }

                    var iconSize = Scale(TabIconSize);
                    var top = (client.Height - iconSize) / 2;
                    var foreground = active ? palette.Text : palette.SecondaryText;
                    if (_favicons?.Invoke(_tabMirror[index].Id) is { } icon)
                    {
                        NativePaint.Image(dc, icon, NativePaint.Rect(x, top, iconSize, iconSize));
                    }
                    else
                    {
                        // The page has no favicon, so the tab leads with the built-in globe from the
                        // same icon font as every other glyph in the shell.
                        NativePaint.Text(dc, GlyphGlobe, NativePaint.Rect(x, 0, iconSize, client.Height),
                            _fonts.SmallIcon, foreground,
                            NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
                    }

                    return x + iconSize + Scale(NativeMetrics.SpaceXSmall);
                }

                textRect.left = DrawLead(textRect.left);
                // The label is always painted and ellipsized into the room that is left, so a tab
                // narrows gradually instead of losing its label at a threshold.
                NativePaint.Text(dc, label, textRect, _fonts.Body,
                    active ? palette.Text : palette.SecondaryText,
                    NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE | NativeMethods.DT_END_ELLIPSIS);
            }

            // A strip that scrolls says so with a thin indicator under the tabs. The track spans the
            // band and the thumb's width is the share of the strip that is visible, so a small thumb
            // means much is off screen and the thumb's place is where the view sits. It is the only
            // affordance the scrolling has, it is drawn only while the strip can scroll, and it
            // leaves with the scroll instead of dimming the tabs at the edges.
            if (_tabStripScrollable)
            {
                var height = Scale(3);
                var inset = Scale(4);
                var track = NativePaint.Rect(_tabScrollOffset + inset, client.Height - inset - height,
                    Math.Max(0, _tabVisibleWidth - inset * 2), height);
                // The thumb never gets so small that it disappears on a long strip.
                var thumbWidth = Math.Min(track.Width,
                    Math.Max(Scale(24), track.Width * _tabVisibleWidth / _tabStripWidth));
                var travel = track.Width - thumbWidth;
                var thumbLeft = track.left + (_tabMaxScroll == 0 ? 0 : travel * _tabScrollOffset / _tabMaxScroll);
                var thumbRadius = height / 2;
                NativePaint.FillRounded(dc, track, thumbRadius, palette.Border, _theme);
                NativePaint.FillRounded(dc, NativePaint.Rect(thumbLeft, track.top, thumbWidth, height),
                    thumbRadius, palette.BorderHover, _theme);
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

    /// <summary>The label a tab shows in the strip, without the loading marker.</summary>
    public string TabLabel(BrowserTab tab) => DisplayTitle(tab).Trim();

    /// <summary>
    /// A tab's label. A private tab already carries its privacy mark as its leading icon, so its
    /// label stays the page's own title: a second "private" word in the label would repeat the mark.
    /// </summary>
    private string DisplayTitle(BrowserTab tab)
    {
        if (string.IsNullOrWhiteSpace(tab.Url)) return _localization.Get("tab.new");

        if (!string.IsNullOrWhiteSpace(tab.Title) && !string.Equals(tab.Title, "New Tab", StringComparison.Ordinal))
        {
            return tab.Title;
        }

        return FallbackTitle(tab.Url);
    }

    /// <summary>A readable label for a URL whose document title is not known yet.</summary>
    private string FallbackTitle(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
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
}
