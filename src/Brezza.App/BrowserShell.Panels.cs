using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Brezza.App.Native;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Navigation;
using Brezza.Core.Permissions;
using Brezza.Core.Privacy;
using Brezza.Core.Tabs;
using Brezza.Persistence;
using Brezza.WebView2;

namespace Brezza.App;

/// <summary>The toolbar panels: opening them, the entries they list, and the actions they raise.</summary>
internal sealed partial class BrowserShell
{
    // ---------- Panels ----------

    private void OpenPanel(string mode)
    {
        if (mode is not ("settings" or "history" or "bookmarks" or "cookies" or "data" or "downloads")) return;
        if (_panel is null)
        {
            _panel = new InternalPanelWindow(_localization, _window.Handle, _theme);
            _panel.SaveSettingsRequested += (_, submission) => SaveSettings(
                submission.SearchTemplate, submission.Language, submission.Theme, submission.StartupBehavior,
                submission.DownloadsFolder);
            _panel.OpenUrlRequested += (_, url) => Observe(async () => await NavigateAsync(url));
            _panel.RemoveEntryRequested += (_, url) => RemoveEntry(url);
            _panel.RemoveCookieRequested += (_, cookie) => RemoveCookie(cookie);
            _panel.ClearHistoryRequested += (_, _) => ClearHistory();
            _panel.ClearBrowsingDataRequested += (_, kinds) => ClearBrowsingData(kinds);
            _panel.BrowseDownloadFolderRequested += (_, _) => BrowseDownloadFolder();
            _panel.OpenDownloadFileRequested += (_, path) => OpenDownloadFile(path);
            _panel.OpenDownloadFolderRequested += (_, path) => OpenDownloadFolder(path);
            _panel.RemoveDownloadRequested += (_, downloadId) => RemoveDownload(downloadId);
            _panel.PauseDownloadRequested += (_, path) => PauseDownload(path);
            _panel.ResumeDownloadRequested += (_, path) => ResumeDownload(path);
            _panel.CancelDownloadRequested += (_, path) => CancelDownload(path);
            _panel.ClearDownloadsRequested += (_, _) => ClearDownloads();
            _panel.CloseRequested += (_, _) => Observe(async () => await ClosePanelAsync());
        }

        _panelMode = mode;
        ShowPanel(mode);
    }

    private void ShowPanel(string mode)
    {
        if (_panel is null) return;
        switch (mode)
        {
            case "settings":
                _panel.ShowSettings(_settings?.SearchTemplate ?? string.Empty, _settings?.Language ?? string.Empty,
                    _settings?.Theme ?? BrowserThemeMode.System,
                    _settings?.StartupBehavior ?? BrowserStartupBehavior.NewTab,
                    EffectiveDownloadsFolder);
                break;
            case "history":
                _panel.ShowEntries(InternalPanelMode.History, HistoryEntries());
                break;
            case "bookmarks":
                _panel.ShowEntries(InternalPanelMode.Bookmarks, BookmarkEntries());
                break;
            case "cookies":
                // The cookie list is read from the profile, so this panel opens from an async query.
                Observe(async () => await ShowCookieEntriesAsync());
                break;
            case "data":
                _panel.ShowEntries(InternalPanelMode.BrowsingData, DataCategoryEntries());
                break;
            case "downloads":
                _panel.ShowEntries(InternalPanelMode.Downloads, DownloadEntries());
                break;
        }

        RenderState();
    }

    /// <summary>
    /// Reads the profile's cookies and lists them. The panel is not reopened when the user navigated
    /// or switched panels while the profile was being read.
    /// </summary>
    private async Task ShowCookieEntriesAsync()
    {
        if (_panel is null || _dataManager is null) return;
        var cookies = await _dataManager.GetCookiesAsync().ConfigureAwait(true);
        if (!string.Equals(_panelMode, "cookies", StringComparison.Ordinal)) return;
        _panel.ShowEntries(InternalPanelMode.Cookies, CookieEntries(cookies));
    }

    private Task ClosePanelAsync()
    {
        // The panel is an owned window that took the foreground when it opened. Windows does not
        // always hand the foreground back when an owned window closes, and never does after a modal
        // confirmation was shown in between, so the shell returns focus to the browser itself.
        _window.Activate();

        ClosePanel();

        // Keyboard focus returns to the page, as it was before the panel opened.
        _observedHost?.Focus();

        RenderState();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Closes the open panel and forgets its mode, so the shell never reports a panel that is gone.
    /// Navigating or switching tabs closes a toolbar view; closing it is not an error.
    /// </summary>
    private void ClosePanel()
    {
        _panelMode = null;
        _panel?.Close();
    }

    private void SaveSettings(string searchTemplate, string language, BrowserThemeMode theme,
        BrowserStartupBehavior startupBehavior, string downloadsFolder)
    {
        if (_settingsStore is null) return;
        try
        {
            var updated = new BrowserSettings
            {
                SearchTemplate = NavigationInputParser.ValidateTemplate(searchTemplate),
                Language = BrowserSettings.NormalizeLanguage(language),
                Theme = BrowserSettings.NormalizeTheme(theme),
                StartupBehavior = BrowserSettings.NormalizeStartupBehavior(startupBehavior),
                DownloadsFolder = BrowserSettings.NormalizeDownloadsFolder(downloadsFolder),
            };
            _settingsStore.Save(updated);
            _settings = updated;
            _inputParser = new NavigationInputParser(updated.SearchTemplate);
            // The factory resolves the folder per download, so a new one applies to the next
            // download without recreating the environments or restarting the browser.
            if (_hostFactory is not null) _hostFactory.DownloadsFolder = updated.DownloadsFolder;
            _localization.LoadLanguage(EffectiveLanguage);
            _window.ApplyLanguage();
            _panel?.ApplyLanguage();
            if (_noticeKey is not null) _window.SetNotice(_noticeKey, _noticeDetail, _noticeAction);
            RenderState();
            // The theme goes last: its synchronous repaint then shows the new language and palette
            // together. Every window follows the shared source; nothing is pushed per window.
            _theme.SetMode(updated.Theme);
            _panel?.SetSettingsResult(true);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is UnauthorizedAccessException)
        {
            TraceWarning("Settings could not be saved", exception);
            _panel?.SetSettingsResult(false);
        }
    }

    /// <summary>
    /// Shows the system folder picker for the downloads folder. The panel owns the dialog, so it
    /// stays disabled until a folder is picked or the dialog is dismissed, and the field is only
    /// written when a folder was actually chosen.
    /// </summary>
    private void BrowseDownloadFolder()
    {
        if (_panel is null || !_panel.IsOpen) return;
        var picked = NativeFolderPicker.TryPick(
            _panel.Handle, _settings?.DownloadsFolder, _localization.Get("settings.downloadFolderBrowse"));
        if (picked is null) return;
        _panel.SetDownloadFolder(picked);
    }

    private void RemoveEntry(string url)
    {
        var mode = _panelMode;
        if (mode == "history")
        {
            if (_historyStore?.Remove(url) == true) TrySave(() => _historyStore.Save(), "history");
        }
        else if (mode == "bookmarks")
        {
            if (_bookmarkStore?.Remove(url) == true) TrySave(() => _bookmarkStore.Save(), "bookmarks");
        }

        RefreshPanel();
    }

    private void ClearHistory()
    {
        _historyStore?.Clear();
        if (_historyStore is not null) TrySave(() => _historyStore.Save(), "history");
        RefreshPanel();
    }

    /// <summary>
    /// Clears the browsing-data categories the user confirmed. Clearing cookies and clearing one
    /// category reach the profile the same way; the outcome is reported through a notice, because
    /// the panel can close while the clear is still running.
    /// </summary>
    private void ClearBrowsingData(BrowserBrowsingDataKind kinds)
    {
        if (_dataManager is null) return;
        Observe(async () =>
        {
            var result = await _dataManager.ClearAsync(kinds).ConfigureAwait(true);
            // The categories stay listed, but the cookies they cleared are gone: the panel is shown
            // again so it reflects what the profile now holds.
            if (result.IsComplete) RefreshPanel();
            var notice = !result.IsComplete
                ? "notice.operationFailed"
                : kinds == BrowserBrowsingDataKind.Cookies
                    ? "notice.cookiesCleared"
                    : "notice.dataCleared";
            SendNotice(notice, result.Detail);
        });
    }

    /// <summary>
    /// Deletes one cookie the user picked in the cookies panel. A single cookie goes without a
    /// confirmation, like a bookmark or a history entry.
    /// </summary>
    private void RemoveCookie(BrowserCookie cookie)
    {
        if (_dataManager is null) return;
        Observe(async () =>
        {
            var result = await _dataManager.RemoveCookieAsync(cookie).ConfigureAwait(true);
            if (result.IsComplete) RefreshPanel();
            else SendNotice("notice.operationFailed", result.Detail);
        });
    }

    private void RefreshPanel()
    {
        if (_panel is null || _panelMode is null) return;
        ShowPanel(_panelMode);
    }
}
