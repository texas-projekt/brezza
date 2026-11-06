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

/// <summary>Mode visibility and layout of the panel controls, in physical pixels.</summary>
internal sealed partial class InternalPanelWindow
{
    // ---------- Mode and layout ----------

    private string TitleKey() => _mode switch
    {
        InternalPanelMode.Settings => "settings.title",
        InternalPanelMode.History => "history.title",
        InternalPanelMode.Cookies => "cookies.title",
        InternalPanelMode.Downloads => "downloads.title",
        InternalPanelMode.BrowsingData => "data.title",
        _ => "bookmarks.title",
    };

    private void ApplyModeVisibility()
    {
        foreach (var control in SettingsControls)
            NativeMethods.ShowWindow(control, IsSettings ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        foreach (var control in FormControls)
            NativeMethods.ShowWindow(control, IsSettings || IsBrowsingData ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        // The browsing-data panel is a list of fixed categories, so it shows the list without the
        // search field that filters an entry list.
        foreach (var control in new[] { _listView, _emptyText })
            NativeMethods.ShowWindow(control, IsList || IsBrowsingData ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        NativeMethods.ShowWindow(_filterEdit, IsList ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        NativeMethods.ShowWindow(_clearButton,
            _mode is InternalPanelMode.History or InternalPanelMode.Cookies or InternalPanelMode.Downloads
                ? NativeMethods.SW_SHOW
                : NativeMethods.SW_HIDE);
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
        var margin = Scale(NativeMetrics.SpaceXLarge);
        var width = Math.Max(Scale(280), client.Width - margin * 2);
        var y = margin;

        var headingHeight = NativeFonts.Measure(_hwnd, _fonts.Heading, "Ag").y;
        NativeMethods.MoveWindow(_titleText, margin, y, width, headingHeight, true);
        y += headingHeight + Scale(4);
        var bodyHeight = NativeFonts.Measure(_hwnd, _fonts.Body, "Ag").y;
        NativeMethods.MoveWindow(_subtitleText, margin, y, width, bodyHeight, true);
        y += bodyHeight + Scale(NativeMetrics.SpaceXLarge);

        var footerTop = client.Height - Scale(FooterHeight);
        if (IsSettings) LayoutSettings(margin, y, width, bodyHeight);
        else LayoutList(margin, y, width, IsBrowsingData ? footerTop : client.Height - margin);
        if (IsSettings || IsBrowsingData) LayoutFooter(client.Width, footerTop);
        UpdateListActions();
    }

    private void LayoutSettings(int left, int top, int width, int bodyHeight)
    {
        // The label column fits the longest localized label, so translations never clip or overlap.
        var rows = new[]
        {
            (_presetLabel, _presetCombo), (_templateLabel, _templateEdit),
            (_languageLabel, _languageCombo), (_themeLabel, _themeCombo),
            (_startupLabel, _startupCombo),
        };

        // The downloads folder's label is measured with the rest, so its own row is not narrower
        // than the column the other rows use.
        var labelWidth = MeasureLabelColumn(rows.Concat(new[] { (_downloadFolderLabel, _downloadFolderButton) }).ToArray(), width);
        var fieldLeft = left + labelWidth;
        var fieldWidth = width - labelWidth;
        var y = top;
        foreach (var (label, field) in rows)
        {
            var fieldHeight = PlaceField(field, fieldLeft, y, fieldWidth);
            NativeMethods.MoveWindow(label, left, y + (fieldHeight - bodyHeight) / 2, labelWidth, bodyHeight, true);
            y += fieldHeight + Scale(NativeMetrics.SpaceLarge);
        }

        // The downloads folder is the one row with a field and a button beside it: the button keeps
        // its own width and the path takes what is left, so a long path stays readable.
        var browseWidth = TextButtonWidth(_downloadFolderButton);
        var folderHeight = PlaceField(_downloadFolderEdit, fieldLeft, y,
            Math.Max(Scale(120), fieldWidth - browseWidth - Scale(NativeMetrics.SpaceSmall)));
        NativeMethods.MoveWindow(_downloadFolderButton,
            fieldLeft + fieldWidth - browseWidth, y, browseWidth, folderHeight, true);
        NativeMethods.MoveWindow(_downloadFolderLabel, left, y + (folderHeight - bodyHeight) / 2, labelWidth, bodyHeight, true);
        y += folderHeight + Scale(NativeMetrics.SpaceLarge);

        NativeMethods.MoveWindow(_statusText, fieldLeft, y, fieldWidth, bodyHeight, true);
    }

    /// <summary>Width of a form's label column: its longest localized label, capped at half the form.</summary>
    private int MeasureLabelColumn((IntPtr Label, IntPtr Field)[] rows, int width)
    {
        var labelWidth = rows.Max(row => NativeFonts.Measure(_hwnd, _fonts.Body, NativeMethods.GetWindowText(row.Label)).x);
        return Math.Min(labelWidth + Scale(16), width / 2);
    }

    private void LayoutList(int left, int top, int width, int bottom)
    {
        // Clearing a whole list is a list-level action, so it sits beside the search field instead
        // of among the row actions.
        var searchWidth = width;
        if (_mode is InternalPanelMode.History or InternalPanelMode.Cookies or InternalPanelMode.Downloads)
        {
            var clearWidth = TextButtonWidth(_clearButton);
            searchWidth = width - clearWidth - Scale(NativeMetrics.SpaceSmall);
            NativeMethods.MoveWindow(_clearButton, left + searchWidth + Scale(NativeMetrics.SpaceSmall), top, clearWidth, Scale(NativeMetrics.ControlHeight), true);
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
        var height = Scale(NativeMetrics.ControlHeight);
        // An edit takes the row height directly; a drop-down list's window height is its open list,
        // and the field renderer sizes the closed field to the row.
        var isEdit = field == _templateEdit || field == _filterEdit || field == _downloadFolderEdit;
        NativeMethods.MoveWindow(field, left, top, width, isEdit ? height : Scale(240), true);
        return height;
    }

    private void LayoutFooter(int clientWidth, int footerTop)
    {
        var margin = Scale(NativeMetrics.SpaceXLarge);
        var buttonHeight = Scale(NativeMetrics.ControlHeight);
        var gap = Scale(NativeMetrics.SpaceSmall);
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
