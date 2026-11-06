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

/// <summary>The list panels: owner-drawn rows, row buttons, row actions, and confirmations.</summary>
internal sealed partial class InternalPanelWindow
{
    // ---------- History, bookmarks, and cookies ----------

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

        NativeMethods.SetWindowText(_emptyText, _localization.Get(_entries.Count > 0
            ? "list.noMatches"
            : _mode switch
            {
                InternalPanelMode.Cookies => "cookies.empty",
                InternalPanelMode.Downloads => "downloads.empty",
                InternalPanelMode.History => "history.empty",
                _ => "bookmarks.empty",
            }));
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
            NativePaint.FillRounded(item.hDC, selection, Scale(NativeMetrics.RadiusControl), palette.Selection, _theme);
        }

        var entry = _visible[(int)item.itemID];
        var row = (int)item.itemID;
        var text = rect;
        text.left += Scale(14);
        // Space for the row buttons is always reserved, so text does not shift on hover.
        text.right = RowButtonsRight(rect, RowButtonCountAt(row) - 1) - Scale(8);
        if (row == _hotRow || (item.itemState & NativeMethods.ODS_SELECTED) != 0)
        {
            DrawRowButtons(item.hDC, rect, entry, row, palette);
        }

        DrawDownloadProgress(item.hDC, rect, entry);

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

    /// <summary>Draws the buttons a row shows: remove always, and what its entry acts on.</summary>
    private void DrawRowButtons(IntPtr hDC, NativeMethods.RECT row, PanelEntry entry, int index, NativeThemePalette palette)
    {
        var download = entry.Download;
        var hot = index == _hotRow;

        // A running download pauses or resumes and cancels; a finished one opens its folder.
        if (IsDownloads && download is { IsRunning: true })
        {
            var live = LiveButtonRect(row);
            if (hot && _hotButton == (download.State == BrowserDownloadState.Paused ? PanelRowButton.Resume : PanelRowButton.Pause))
            {
                NativePaint.FillRounded(hDC, live, Scale(NativeMetrics.RadiusControl), palette.Hover, _theme);
            }

            NativePaint.Text(hDC, download.State == BrowserDownloadState.Paused ? GlyphResume : GlyphPause, live,
                _fonts.SmallIcon, palette.SecondaryText, NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);

            var cancel = CancelButtonRect(row);
            if (hot && _hotButton == PanelRowButton.Cancel)
            {
                NativePaint.FillRounded(hDC, cancel, Scale(NativeMetrics.RadiusControl), palette.Hover, _theme);
            }

            NativePaint.Text(hDC, GlyphCancel, cancel, _fonts.SmallIcon, palette.SecondaryText,
                NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        }
        else if (IsDownloads)
        {
            var folder = FolderButtonRect(row);
            if (hot && _hotButton == PanelRowButton.Folder)
            {
                NativePaint.FillRounded(hDC, folder, Scale(NativeMetrics.RadiusControl), palette.Hover, _theme);
            }

            NativePaint.Text(hDC, GlyphFolder, folder, _fonts.SmallIcon, palette.SecondaryText,
                NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        }

        var button = RemoveButtonRect(row);
        if (hot && _hotButton == PanelRowButton.Remove)
        {
            NativePaint.FillRounded(hDC, button, Scale(NativeMetrics.RadiusControl), palette.Hover, _theme);
        }

        NativePaint.Text(hDC, GlyphRemove, button, _fonts.SmallIcon, palette.SecondaryText,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
    }

    /// <summary>
    /// Draws a thin bar along the bottom of a running download's row, filled by the share that has
    /// arrived. It is drawn only when the runtime reported a total, because a share of an unknown
    /// size is not a share.
    /// </summary>
    private void DrawDownloadProgress(IntPtr hDC, NativeMethods.RECT row, PanelEntry entry)
    {
        var download = entry.Download;
        if (!IsDownloads || download is not { IsRunning: true } || download.TotalBytes is not { } total || total == 0) return;

        var received = download.ReceivedBytes ?? 0;
        var share = received >= total ? 1 : (double)received / total;
        var height = Math.Max(Scale(2), Scale(3));
        var track = NativePaint.Rect(row.left + Scale(4), row.bottom - height - Scale(2), row.Width - Scale(8), height);
        NativePaint.FillRounded(hDC, track, height / 2, _theme.Palette.Border, _theme);
        var filled = track;
        filled.right = filled.left + (int)(track.Width * share);
        if (filled.right > filled.left)
        {
            NativePaint.FillRounded(hDC, filled, height / 2, _theme.Palette.Accent, _theme);
        }
    }

    private void UpdateListActions()
    {
        if (_clearButton == IntPtr.Zero) return;
        NativeMethods.EnableWindow(_clearButton,
            _entries.Count > 0 && _mode is InternalPanelMode.History or InternalPanelMode.Cookies or InternalPanelMode.Downloads);
    }

    private int SelectedIndex() =>
        (int)NativeMethods.SendMessage(_listView, NativeMethods.LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);

    private void Open(int index)
    {
        if (index < 0 || index >= _visible.Count) return;
        var entry = _visible[index];
        // A cookie or a data category has no page to open; its row button is the only action.
        if (IsSelfActing) return;
        if (IsDownloads)
        {
            // Only a finished file can be opened; a running or failed download has nothing yet.
            if (entry.Download is { State: BrowserDownloadState.Completed } download)
            {
                OpenDownloadFileRequested?.Invoke(this, download.TargetFilePath);
            }

            return;
        }

        OpenUrlRequested?.Invoke(this, entry.Url);
    }

    /// <summary>Opens the folder a download was written to, whatever state the download is in.</summary>
    private void OpenFolder(int index)
    {
        if (index < 0 || index >= _visible.Count) return;
        if (_visible[index].Download is { } download)
        {
            OpenDownloadFolderRequested?.Invoke(this, download.TargetFilePath);
        }
    }

    /// <summary>Pauses or resumes a running download.</summary>
    private void PauseOrResume(int index, bool paused)
    {
        if (index < 0 || index >= _visible.Count) return;
        var download = _visible[index].Download;
        if (download is not { IsRunning: true }) return;
        if (paused && download.State == BrowserDownloadState.Paused) return;
        if (!paused && download.State == BrowserDownloadState.InProgress) return;
        var requested = paused ? PauseDownloadRequested : ResumeDownloadRequested;
        requested?.Invoke(this, download.TargetFilePath);
    }

    /// <summary>
    /// Cancels a download. The partial file is dropped by the runtime, so this is the one download
    /// action that asks first: it destroys bytes the user already waited for.
    /// </summary>
    private void Cancel(int index)
    {
        if (index < 0 || index >= _visible.Count) return;
        var download = _visible[index].Download;
        if (download is not { IsRunning: true }) return;
        if (!Confirm("downloads.cancelConfirm", "downloads.cancel")) return;
        CancelDownloadRequested?.Invoke(this, download.TargetFilePath);
    }

    private void Remove(int index)
    {
        if (index < 0 || index >= _visible.Count) return;
        var entry = _visible[index];
        if (IsDownloads)
        {
            // A list entry is the user's own record, so it goes without a confirmation, like a
            // bookmark or a history entry. The downloaded file itself is never touched.
            if (entry.Download is { } download) RemoveDownloadRequested?.Invoke(this, download.Id);
            return;
        }

        if (IsCookies)
        {
            // A single cookie goes without a confirmation, like a bookmark or a history entry.
            if (entry.Cookie is { } cookie) RemoveCookieRequested?.Invoke(this, cookie);
            return;
        }

        if (IsBrowsingData)
        {
            // Clearing a category removes site data, so it asks first, like clearing history does.
            if (entry.DataKind is { } kind) ConfirmClearCategory(kind);
            return;
        }

        RemoveEntryRequested?.Invoke(this, entry.Url);
    }

    /// <summary>The rect of a list row's nth button, counted from the right edge.</summary>
    private NativeMethods.RECT RowButtonRect(NativeMethods.RECT row, int index)
    {
        var size = Scale(RemoveButtonSize);
        var left = row.right - size - Scale(10) - index * (size + Scale(4));
        return NativePaint.Rect(left, row.top + (row.Height - size) / 2, size, size);
    }

    /// <summary>The remove button every list row shows, rightmost.</summary>
    private NativeMethods.RECT RemoveButtonRect(NativeMethods.RECT row) => RowButtonRect(row, 0);

    /// <summary>The cancel button a running download shows beside removing the entry.</summary>
    private NativeMethods.RECT CancelButtonRect(NativeMethods.RECT row) => RowButtonRect(row, 1);

    /// <summary>The pause or resume button a running download shows, leftmost of its buttons.</summary>
    private NativeMethods.RECT LiveButtonRect(NativeMethods.RECT row) => RowButtonRect(row, 2);

    /// <summary>The open-folder button a finished download shows left of its remove button.</summary>
    private NativeMethods.RECT FolderButtonRect(NativeMethods.RECT row) => RowButtonRect(row, 1);

    /// <summary>The left edge of the rightmost button a row shows, so text never runs under one.</summary>
    private int RowButtonsRight(NativeMethods.RECT row, int index) => RowButtonRect(row, index).left;

    /// <summary>How many buttons a row shows, counted from the right edge.</summary>
    private int RowButtonCount(BrowserDownload? download) =>
        IsDownloads && download is { IsRunning: true } ? 3 : IsDownloads ? 2 : 1;

    /// <summary>How many buttons the row at <paramref name="index"/> shows.</summary>
    private int RowButtonCountAt(int index) =>
        index >= 0 && index < _visible.Count ? RowButtonCount(_visible[index].Download) : 1;

    private static bool IsInRect(int x, int y, NativeMethods.RECT rect) =>
        x >= rect.left && x < rect.right && y >= rect.top && y < rect.bottom;

    private NativeMethods.RECT RowRect(int index)
    {
        var rect = new NativeMethods.RECT();
        NativeMethods.SendMessage(_listView, NativeMethods.LB_GETITEMRECT, new IntPtr(index), ref rect);
        return rect;
    }

    /// <summary>The row under a list-box client point, and which of its buttons the point is on.</summary>
    private (int Row, PanelRowButton Button) HitTestRow(IntPtr lParam)
    {
        var result = NativeMethods.SendMessage(_listView, NativeMethods.LB_ITEMFROMPOINT, IntPtr.Zero, lParam).ToInt64();
        var index = (int)(result & 0xFFFF);
        if ((result >> 16) != 0 || index >= _visible.Count) return (-1, PanelRowButton.None);
        var x = unchecked((short)NativeMethods.GetLoWord(lParam));
        var y = unchecked((short)NativeMethods.GetHiWord(lParam));
        var row = RowRect(index);
        var download = _visible[index].Download;
        if (IsInRect(x, y, RemoveButtonRect(row))) return (index, PanelRowButton.Remove);
        if (IsDownloads && download is { IsRunning: true })
        {
            if (IsInRect(x, y, CancelButtonRect(row))) return (index, PanelRowButton.Cancel);
            if (IsInRect(x, y, LiveButtonRect(row)))
            {
                return (index, download.State == BrowserDownloadState.Paused ? PanelRowButton.Resume : PanelRowButton.Pause);
            }
        }
        else if (IsDownloads && IsInRect(x, y, FolderButtonRect(row)))
        {
            return (index, PanelRowButton.Folder);
        }

        return (index, PanelRowButton.None);
    }

    private void SetHotRow(int row, PanelRowButton button)
    {
        if (row == _hotRow && button == _hotButton) return;
        var previous = _hotRow;
        _hotRow = row;
        _hotButton = button;
        InvalidateRow(previous);
        if (row != previous) InvalidateRow(row);
    }

    private void InvalidateRow(int row)
    {
        if (row < 0) return;
        var rect = RowRect(row);
        NativeMethods.InvalidateRect(_listView, ref rect, false);
    }

    /// <summary>Row hover, click to open, the per-row buttons, and the Delete key.</summary>
    private IntPtr OnListMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        try
        {
            return OnListMessageCore(hWnd, msg, wParam, lParam);
        }
        catch (Exception exception)
        {
            // The list is a system control; an exception must not escape its subclass.
            Trace.TraceError("The panel list message {0} failed: {1}", msg, exception);
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr OnListMessageCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEMOVE:
                var hover = HitTestRow(lParam);
                SetHotRow(hover.Row, hover.Button);
                var track = new NativeMethods.TRACKMOUSEEVENT
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                    dwFlags = NativeMethods.TME_LEAVE,
                    hwndTrack = hWnd,
                };
                NativeMethods.TrackMouseEvent(ref track);
                break;

            case NativeMethods.WM_MOUSELEAVE:
                SetHotRow(-1, PanelRowButton.None);
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
                    switch (release.Button)
                    {
                        case PanelRowButton.Folder:
                            OpenFolder(release.Row);
                            break;
                        case PanelRowButton.Pause:
                            PauseOrResume(release.Row, paused: true);
                            break;
                        case PanelRowButton.Resume:
                            PauseOrResume(release.Row, paused: false);
                            break;
                        case PanelRowButton.Cancel:
                            Cancel(release.Row);
                            break;
                        case PanelRowButton.Remove:
                            Remove(release.Row);
                            break;
                        default:
                            Open(release.Row);
                            break;
                    }
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
        if (_entries.Count == 0) return;
        if (_mode == InternalPanelMode.History)
        {
            if (Confirm("history.clearConfirm", "history.clear")) ClearHistoryRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsDownloads)
        {
            // Clearing the list removes the records, never the files that were downloaded.
            if (Confirm("downloads.clearConfirm", "downloads.clear")) ClearDownloadsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsCookies && Confirm("cookies.clearConfirm", "cookies.clear"))
        {
            ClearBrowsingDataRequested?.Invoke(this, BrowserBrowsingDataKind.Cookies);
        }
    }

    /// <summary>Asks before one category of browsing data is cleared, naming that category.</summary>
    private void ConfirmClearCategory(BrowserBrowsingDataKind kind)
    {
        if (Confirm("data.clearCategoryConfirm", CategoryTitleKey(kind)))
        {
            ClearBrowsingDataRequested?.Invoke(this, kind);
        }
    }

    private void ConfirmClearAll()
    {
        if (Confirm("data.clearAllConfirm", "data.clearAll"))
        {
            ClearBrowsingDataRequested?.Invoke(this, BrowserBrowsingDataKind.All);
        }
    }

    /// <summary>Asks a yes/no question; true when the user confirmed.</summary>
    private bool Confirm(string messageKey, string titleKey)
    {
        var answer = NativeMethods.MessageBox(
            _hwnd,
            _localization.Get(messageKey),
            _localization.Get(titleKey),
            NativeMethods.MB_YESNO | NativeMethods.MB_ICONQUESTION);
        return answer == NativeMethods.IDYES;
    }

    /// <summary>The localized name of a browsing-data category, used as a confirmation's title.</summary>
    private string CategoryTitleKey(BrowserBrowsingDataKind kind) => kind switch
    {
        BrowserBrowsingDataKind.Cache => "data.category.cache",
        BrowserBrowsingDataKind.SiteData => "data.category.siteData",
        BrowserBrowsingDataKind.ServiceWorkers => "data.category.serviceWorkers",
        _ => "data.category.cookies",
    };
}
