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

/// <summary>Layout of the caption row, toolbar, notice bar, and new-tab page, in physical pixels.</summary>
internal sealed partial class MainWindow
{
    // ---------- Layout ----------

    private int Scale(int value) => NativeMethods.Scale(value, _dpi);

    private void Layout()
    {
        if (_hwnd == IntPtr.Zero || _tabStrip == IntPtr.Zero) return;
        NativeMethods.GetClientRect(_hwnd, out var client);
        var width = Math.Max(0, client.Width);
        LayoutCaptionRow(width);
        // A resize can leave the active tab outside the space the strip shows; bring it back.
        EnsureTabVisible(SelectedTabIndex);
        LayoutToolbar(width);
        LayoutNotice(width);

        var bounds = GetContentBounds();
        ContentBoundsChanged?.Invoke(this, bounds);
        LayoutStartPage(bounds);
    }

    private void LayoutCaptionRow(int width)
    {
        var captionButtonWidth = Scale(CaptionButtonWidth);
        // Caption buttons span the whole caption row, top to bottom, so their hover fill lines up
        // with the full height of the tab band behind them.
        var captionButtonHeight = Scale(CaptionHeight);
        NativeMethods.MoveWindow(_titleMinimize, width - captionButtonWidth * 3, 0, captionButtonWidth, captionButtonHeight, true);
        NativeMethods.MoveWindow(_titleMaximize, width - captionButtonWidth * 2, 0, captionButtonWidth, captionButtonHeight, true);
        NativeMethods.MoveWindow(_titleClose, width - captionButtonWidth, 0, captionButtonWidth, captionButtonHeight, true);

        var stripRight = SizeTabStrip(width, _tabMirror.Count);
        var tabTop = Scale(TabTop);
        var tabHeight = Scale(CaptionHeight - TabTop);
        var actionSize = Scale(NativeMetrics.ControlHeightCompact);
        var actionTop = tabTop + (tabHeight - actionSize) / 2;
        NativeMethods.MoveWindow(_newTabButton, stripRight + Scale(NativeMetrics.SpaceXSmall), actionTop, actionSize, actionSize, true);
        PositionTabClose();
    }

    /// <summary>
    /// Sizes the tabs and the strip for a tab count and returns the strip's visible right edge. Tabs
    /// share the space left of the new-tab button and a minimum drag area, and never shrink below
    /// <see cref="MinimumTabWidth"/>: past that point the strip is wider than the space it shows and
    /// scrolls, so a title stays readable and every tab keeps its close target. A tab control whose
    /// items overflow scrolls them and adds its own system scroll arrows, which the custom painting
    /// cannot follow, so the strip window itself is moved to scroll instead and those arrows are
    /// never given a reason to appear; scrolling has no visible control and follows the active tab,
    /// which the wheel, a click on a partly visible tab, and Ctrl+Tab all move. <see cref="UpdateTabs"/>
    /// calls this before inserting items, so the strip is never laid out for the wrong count.
    /// </summary>
    private int SizeTabStrip(int width, int tabCount)
    {
        var tabTop = Scale(TabTop);
        var tabHeight = Scale(CaptionHeight - TabTop);
        var stripLeft = Scale(NativeMetrics.SpaceSmall);
        var actionsWidth = Scale(NativeMetrics.SpaceXSmall) + Scale(NativeMetrics.ControlHeightCompact) + Scale(NativeMetrics.SpaceXSmall);
        var available = Math.Max(MinimumTabWidth,
            width - stripLeft - actionsWidth - Scale(CaptionButtonWidth) * 3 - Scale(48));
        var count = Math.Max(1, tabCount);
        // The control indents the first item and widens the selected one, so keep some slack.
        var slack = Scale(8);
        // Dividing the room down rounds, so the tabs would fall a few pixels short of the band and
        // the new-tab button would jiggle left and right as tabs are added. Rounding up instead
        // fills the band as soon as the tabs reach it, and past that point the button stays put:
        // the strip is wider than the room it shows, and it scrolls instead of resizing.
        var share = (available - slack + count - 1) / count;
        var tabWidth = Math.Max(MinimumTabWidth, Math.Min(Scale(MaximumTabWidth), share));
        // Widen the strip before its items are laid out. The control builds its own system scroll
        // arrows the moment its items do not fit the window it currently has, and it keeps them once
        // built, so a strip widened only after its items are laid out leaves those arrows behind —
        // hidden again at once, but already made, and they flash in the caption row. The window is
        // moved without redrawing and its region still clips it to the band, so the extra width is
        // never painted; it exists only so the items fit while they change.
        MoveTabStrip(Math.Max(_tabStripWidth, tabWidth * count + count + slack), _tabScrollOffset);
        NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_SETITEMSIZE, IntPtr.Zero,
            new IntPtr(NativeMethods.MakeLong(tabWidth, tabHeight)));

        // The control can hold a different count than the one asked for: UpdateTabs sizes the
        // strip before it inserts or deletes its items. Whatever the control holds right now has
        // to fit the window from this moment on, because the arrows are built the moment it does
        // not — and they stay built, hidden or not, for the life of the control.
        var heldCount = (int)NativeMethods.SendMessage(
            _tabStrip, NativeMethods.TCM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        // Ask the control where its items really end instead of guessing. The control indents the
        // first item and pads each one, so tabWidth * count falls short as tabs are added, and a
        // strip whose items overflow makes the control build its own system scroll arrows — the one
        // control the strip must never show. The last item's right edge is how far the items reach,
        // and the window's region clips it to the band anyway, so room the items do not use costs
        // nothing.
        var heldEnd = heldCount > 0 ? TabRect(heldCount - 1).right : 0;
        if (heldCount == count)
        {
            // The control already holds the requested count, so the measurement is the width the
            // strip keeps: the real end of the items, and nothing wider.
            _tabStripWidth = Math.Max(tabWidth * count + slack, heldEnd + slack);
        }
        else
        {
            // The items change under this window before it is sized again. Keep room for both the
            // items the control holds now and the ones it is about to hold, at the new item size:
            // narrowing now overflows whichever set is present and builds the system scroll arrows
            // this design exists to avoid — a closed tab's old items on the way out, or a new
            // tab's incoming ones on the way in. When the control holds the previous count, one
            // more item past the measured end is exact; the estimate otherwise carries the
            // control's own padding plus the slack. Layout re-sizes from the real item rectangle
            // once the items are in place, in the same turn and before any paint.
            var incomingEnd = heldCount == count - 1
                ? heldEnd + tabWidth
                : tabWidth * count + count + slack;
            _tabStripWidth = Math.Max(heldEnd + slack, incomingEnd + slack);
        }
        _tabStripScrollable = _tabStripWidth > available;
        _tabVisibleWidth = Math.Min(available, _tabStripWidth);
        _tabMaxScroll = Math.Max(0, _tabStripWidth - _tabVisibleWidth);
        _tabScrollOffset = Math.Max(0, Math.Min(_tabScrollOffset, _tabMaxScroll));
        // The strip is sized before it is painted, so clip it to the band first and move it without
        // redrawing: a strip that has just grown past the band must never be painted at its full
        // width, or it covers the controls beside it for a frame. The single repaint at the end shows
        // the new size under the new region.
        ClipTabStripToBand(repaintNeighbours: true);
        MoveTabStrip(_tabStripWidth, _tabScrollOffset);
        HideTabScroller();
        NativeMethods.InvalidateRect(_tabStrip, IntPtr.Zero, false);
        _laidOutTabCount = tabCount;
        return stripLeft + _tabVisibleWidth;
    }

    /// <summary>
    /// Sizes and places the strip window without repainting it. Nothing is drawn, so a caller can
    /// put the window where it needs to be and let one later invalidation show the result: the strip
    /// is never painted at a size or an offset it is about to leave.
    /// </summary>
    private void MoveTabStrip(int stripWidth, int offset)
    {
        NativeMethods.SetWindowPos(_tabStrip, NativeMethods.HWND_TOP,
            Scale(NativeMetrics.SpaceSmall) - offset, Scale(TabTop),
            stripWidth, Scale(CaptionHeight - TabTop),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOREDRAW | NativeMethods.SWP_NOCOPYBITS);
    }

    private void LayoutToolbar(int width)
    {
        var size = Scale(NativeMetrics.ControlHeight);
        var gap = Scale(NativeMetrics.SpaceXSmall);
        var top = Scale(CaptionHeight) + (Scale(ToolbarHeight) - size) / 2;
        var x = Scale(NativeMetrics.SpaceSmall);
        foreach (var button in new[] { _backButton, _forwardButton, _reloadButton })
        {
            NativeMethods.MoveWindow(button, x, top, size, size, true);
            x += size + gap;
        }

        var right = width - Scale(NativeMetrics.SpaceSmall);
        foreach (var button in new[] { _menuButton, _bookmarkButton })
        {
            right -= size;
            NativeMethods.MoveWindow(button, right, top, size, size, true);
            right -= gap;
        }

        // A drop-down combo box treats its window height as the open list height; the field renderer
        // sizes the closed field to the toolbar control size.
        var addressLeft = x + Scale(NativeMetrics.SpaceXSmall);
        var addressWidth = Math.Max(Scale(120), right - Scale(NativeMetrics.SpaceXSmall) - addressLeft);
        NativeMethods.MoveWindow(_addressCombo, addressLeft, top, addressWidth, Scale(320), true);
    }

    private void LayoutNotice(int width)
    {
        if (!_noticeVisible) return;
        var top = Scale(CaptionHeight + ToolbarHeight) + 1;
        var height = Scale(NoticeHeight);
        var buttonHeight = Scale(NativeMetrics.ControlHeightCompact);
        var buttonTop = top + (height - buttonHeight) / 2;
        var gap = Scale(NativeMetrics.SpaceSmall);
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
        var left = Scale(NativeMetrics.SpaceLarge);
        NativeMethods.MoveWindow(_noticeText, left, top + (height - textHeight) / 2, Math.Max(Scale(80), right - left), textHeight, true);
    }

    private void LayoutStartPage(ContentBounds bounds)
    {
        var hasEntries = _startEntries.Recent.Count + _startEntries.Bookmarks.Count > 0;
        var width = Math.Min(bounds.Width - Scale(64), Scale(hasEntries ? 720 : 560));
        var left = (bounds.Width - width) / 2;
        var titleHeight = NativeFonts.Measure(_hwnd, _fonts.Display, "Ag").y + Scale(4);
        // With shortcuts below it the title sits higher, so both columns fit in a default window.
        var titleTop = bounds.Y + Math.Max(Scale(32), bounds.Height * (hasEntries ? 2 : 3) / 10);
        NativeMethods.MoveWindow(_startTitleText, left, titleTop, width, titleHeight, true);
        var subtitleTop = titleTop + titleHeight + Scale(12);
        var lineHeight = NativeFonts.Measure(_hwnd, _fonts.Body, "Ag").y;
        var subtitleHeight = hasEntries ? lineHeight * 3 : Scale(96);
        NativeMethods.MoveWindow(_startSubtitleText, left, subtitleTop, width, subtitleHeight, true);
        ShowControls(_startPageVisible, _startTitleText, _startSubtitleText);

        // Two columns of shortcuts, recent pages beside bookmarks. A column with no entries is not
        // shown. Columns are capped at a readable width and centered as a group under the title, so
        // one short column does not hug the left edge of a wide page.
        var columns = new[]
        {
            (Header: _startRecentHeader, First: 0, Count: _startEntries.Recent.Count),
            (Header: _startBookmarksHeader, First: StartEntrySlots, Count: _startEntries.Bookmarks.Count),
        };
        var shown = columns.Count(column => column.Count > 0);
        var gap = Scale(NativeMetrics.SpaceXLarge);
        var columnWidth = shown == 0 ? 0 : Math.Min(Scale(StartColumnMaximumWidth), (width - gap * (shown - 1)) / shown);
        var groupWidth = shown == 0 ? 0 : columnWidth * shown + gap * (shown - 1);
        var columnLeft = (bounds.Width - groupWidth) / 2;
        var sectionTop = subtitleTop + subtitleHeight + Scale(NativeMetrics.SpaceLarge);
        var rowHeight = Scale(NativeMetrics.ControlHeight);
        foreach (var column in columns)
        {
            var visible = _startPageVisible && column.Count > 0;
            ShowControls(visible, column.Header);
            if (visible)
            {
                NativeMethods.MoveWindow(column.Header, columnLeft + Scale(NativeMetrics.SpaceSmall), sectionTop,
                    columnWidth, lineHeight, true);
            }

            for (var slot = 0; slot < StartEntrySlots; slot++)
            {
                var button = _startEntryButtons[column.First + slot];
                var rowVisible = visible && slot < column.Count &&
                    sectionTop + lineHeight + Scale(NativeMetrics.SpaceXSmall) + (slot + 1) * rowHeight <= bounds.Y + bounds.Height;
                ShowControls(rowVisible, button);
                if (!rowVisible) continue;
                NativeMethods.MoveWindow(button, columnLeft,
                    sectionTop + lineHeight + Scale(NativeMetrics.SpaceXSmall) + slot * rowHeight, columnWidth, rowHeight, true);
            }

            if (column.Count > 0) columnLeft += columnWidth + gap;
        }
    }

    // The widest a new-tab shortcut column gets, so its rows stay a readable line length.
    private const int StartColumnMaximumWidth = 340;

    private int TextButtonWidth(IntPtr button) =>
        Math.Max(Scale(72), NativeFonts.Measure(_hwnd, _fonts.Body, NativeMethods.GetWindowText(button)).x + Scale(24));

    private int ContentTop()
    {
        var top = Scale(CaptionHeight + ToolbarHeight) + 1;
        if (_noticeVisible) top += Scale(NoticeHeight);
        return top;
    }
}
