using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Brezza.App.Native;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Navigation;
using Brezza.Core.Permissions;
using Brezza.Core.Tabs;
using Brezza.Persistence;
using Brezza.WebView2;

namespace Brezza.App;

/// <summary>
/// Composition root and browser logic. It owns Core state and the local stores, renders state into
/// the native window, and turns native control intents into Core commands. It never owns a second
/// copy of browser state and never touches WebView2 objects.
/// </summary>
internal sealed partial class BrowserShell : IDisposable
{
    private const int SleepTimerId = 1;
    private const int PrivateCleanupTimerId = 2;

    private readonly Localization _localization;

    // The single theme source. Every native window resolves its palette here, so the main window,
    // panels, pop-ups, and content surfaces never disagree about the active theme.
    private readonly NativeThemeSource _theme = new();
    private readonly MainWindow _window;

    private string _dataRoot = string.Empty;

    private SettingsStore? _settingsStore;
    private BrowserSettings? _settings;
    private NavigationInputParser? _inputParser;
    private HistoryStore? _historyStore;
    private BookmarkStore? _bookmarkStore;
    private SessionStore? _sessionStore;
    private WebView2TabHostFactory? _hostFactory;
    private TabRegistry? _tabs;
    private TabRuntimeManager? _runtime;
    private ITabHost? _observedHost;
    private InternalPanelWindow? _panel;

    private string? _panelMode;
    private bool _panelOwnedByTab;
    private bool _shutdownStarted;

    public BrowserShell()
    {
        _localization = new Localization(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BrowserUi"));
        _window = new MainWindow(_localization, _theme);
        WireWindow();
    }

    /// <summary>Creates the window, starts the browser, and pumps the native message loop.</summary>
    public int Run()
    {
        _window.Create();

        // The saved theme and language are applied before the first frame. Showing the window on
        // the system palette first and re-theming it afterwards reads as white patches at startup
        // and a theme switch that arrives in steps.
        var preferencesLoaded = LoadPreferences();
        _window.Show();
        _window.SetTimerEnabled(SleepTimerId, true);
        if (!preferencesLoaded) return _window.RunMessageLoop();
        InitializeAsync();
        return _window.RunMessageLoop();
    }

    private void WireWindow()
    {
        _window.CloseRequested += (_, _) => Observe(async () => await ShutdownAsync());
        _window.NavigateRequested += (_, text) => Observe(async () => await NavigateAsync(text));
        _window.ActivateTabRequested += (_, tabId) => Observe(async () => await ActivateTabAsync(tabId));
        _window.CloseTabRequested += (_, tabId) => Observe(async () => await CloseTabAsync(tabId));
        _window.NewTabRequested += (_, _) => Observe(async () => await CreateTabAsync());
        _window.NewPrivateTabRequested += (_, _) => Observe(async () => await CreateTabAsync(BrowserSessionKind.Private));
        _window.BackRequested += (_, _) => _runtime?.GoBack();
        _window.ForwardRequested += (_, _) => _runtime?.GoForward();
        _window.ReloadRequested += (_, _) => _runtime?.Reload();
        _window.ToggleBookmarkRequested += (_, _) => ToggleBookmark();
        _window.OpenPanelRequested += (_, mode) => OpenPanel(mode);
        _window.NoticeDismissRequested += (_, _) => SendNotice(null);
        _window.NoticeReloadRequested += (_, _) => _runtime?.Reload();
        _window.ShortcutPressed += OnShortcutPressed;
        _window.TimerTick += OnTimerTick;
        _window.ContentBoundsChanged += OnContentBoundsChanged;
        _window.ThemeChanged += (_, _) => ApplyThemeToContent();
        _window.AddressSuggestionsProvider = GetAddressSuggestions;
    }

    // ---------- Startup ----------

    /// <summary>
    /// Loads the local stores and applies the saved theme and language before the first frame, so
    /// the window never appears in the system palette and is re-themed afterwards.
    /// </summary>
    private bool LoadPreferences()
    {
        try
        {
            _dataRoot = ResolveDataRoot();
            Directory.CreateDirectory(_dataRoot);
            _settingsStore = new SettingsStore(Path.Combine(_dataRoot, "settings.xml"));
            _settings = _settingsStore.Load();
            _inputParser = new NavigationInputParser(_settings.SearchTemplate);
            _historyStore = new HistoryStore(Path.Combine(_dataRoot, "history.xml"));
            _bookmarkStore = new BookmarkStore(Path.Combine(_dataRoot, "bookmarks.xml"));
            _sessionStore = new SessionStore(Path.Combine(_dataRoot, "session.xml"));

            _localization.LoadLanguage(EffectiveLanguage);
            _theme.SetMode(_settings.Theme);
            _window.ApplyLanguage();
            return true;
        }
        catch (Exception exception)
        {
            TraceError("Browser data could not be loaded", exception);
            _window.SetStartupError(exception.Message);
            return false;
        }
    }

    private async void InitializeAsync()
    {
        try
        {
            WebView2Runtime.EnsureAvailable();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _window.SetStartupError(_localization.Get("error.webView2Missing"));
            return;
        }
        catch (Exception exception)
        {
            TraceError("The WebView2 Runtime could not be checked", exception);
            _window.SetStartupError(exception.Message);
            return;
        }

        try
        {
            _hostFactory = new WebView2TabHostFactory(
                Path.Combine(_dataRoot, "WebView2"),
                _window.Handle,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                PaletteSurfaceColor());

            _tabs = new TabRegistry();
            _tabs.Changed += (_, _) => RenderState();
            _runtime = new TabRuntimeManager(_tabs, _hostFactory);
            _runtime.ActiveHostChanged += OnActiveHostChanged;
            _runtime.NavigationCompleted += OnTabNavigationCompleted;
            _runtime.PrivateCleanupCompleted += OnPrivateCleanupCompleted;
            _runtime.PopupRequested += OnPopupRequested;
            _runtime.DownloadStarted += OnDownloadStarted;
            _runtime.DownloadStatusChanged += OnDownloadStatusChanged;
            _runtime.PermissionRequested += OnPermissionRequested;

            var session = _sessionStore!.Load();
            foreach (var tab in session.Tabs!) _tabs.Restore(tab.Id, tab.Url, tab.Title);
            if (_tabs.Tabs.Count == 0) _tabs.Create();

            await _runtime.ActivateAsync(session.ActiveTabId ?? _tabs.Tabs[0].Id);
            RenderState();
#if DEBUG
            var smokeMode = Environment.GetEnvironmentVariable("BREZZA_UI_SMOKE");
            if (!string.IsNullOrWhiteSpace(smokeMode)) await RunDebugSmokeAsync(smokeMode);
#endif
        }
        catch (Exception exception)
        {
            TraceError("The browser could not start", exception);
            _window.SetStartupError(exception.Message);
        }
    }

    // ---------- Commands ----------

    private async Task NavigateAsync(string input)
    { System.Diagnostics.Trace.TraceInformation("NAV: [{0}]", input);
        if (_tabs?.ActiveTab is null || _runtime is null || _inputParser is null) return;

        // A toolbar-opened panel closes when the user navigates; a panel that belongs to this tab
        // stays until its tab leaves the internal route.
        if (_panelMode is not null && !_panelOwnedByTab) _panel?.Close();

        var target = _inputParser.Parse(input);
        if (target.Kind == NavigationTargetKind.Empty || target.Uri is null) return;

        var logicalUrl = NavigationInputParser.IsInternalPage(target.Uri)
            ? NavigationInputParser.GetCanonicalInternalUrl(target.Uri)
            : target.Uri.AbsoluteUri;

        if (NavigationInputParser.IsInternalPage(target.Uri))
        {
            // A browser-owned page is a native panel, never a WebView2 content host.
            if (_runtime is not null) await _runtime.ReleaseHostAsync(_tabs.ActiveTab.Id);
            _tabs.SetUrl(_tabs.ActiveTab.Id, logicalUrl);
            RenderState();
            return;
        }

        _tabs.SetUrl(_tabs.ActiveTab.Id, logicalUrl);
        await _runtime.NavigateAsync(_tabs.ActiveTab.Id, target.Uri, waitForNavigation: false);
        RenderState();
        _runtime.ActiveHost?.Focus();
    }

    private async Task CreateTabAsync(BrowserSessionKind? requestedKind = null)
    {
        if (_tabs is null || _runtime is null) return;
        if (_panelMode is not null && !_panelOwnedByTab) _panel?.Close();
        var sessionKind = requestedKind ?? _tabs.ActiveTab?.SessionKind ?? BrowserSessionKind.Normal;
        var tab = _tabs.Create(sessionKind: sessionKind);
        await _runtime.ActivateAsync(tab.Id, waitForNavigation: false);
        RenderState();
    }

    private async Task ActivateTabAsync(string tabId)
    {
        if (_runtime is null) return;
        if (_panelMode is not null && !_panelOwnedByTab) _panel?.Close();
        await _runtime.ActivateAsync(tabId);
        RenderState();
    }

    private async Task CloseTabAsync(string tabId)
    {
        if (_tabs is null || _runtime is null) return;
        await _runtime.CloseAsync(tabId);
        if (_tabs.Tabs.Count == 0)
        {
            var newTab = _tabs.Create();
            await _runtime.ActivateAsync(newTab.Id, waitForNavigation: false);
        }

        RenderState();
    }

    private void ToggleBookmark()
    {
        if (_runtime?.ActiveHost is not { } host || _bookmarkStore is null) return;
        if (_bookmarkStore.Contains(host.Source)) _bookmarkStore.Remove(host.Source);
        else _bookmarkStore.Add(host.Source, host.Title);
        RenderState();
    }

    private void OnShortcutPressed(object? sender, BrowserShortcutEventArgs e)
    {
        if (_tabs is null || _runtime is null) return;
        if (e.Control)
        {
            if (e.VirtualKey == 0x4E && e.Shift)
            {
                e.Handled = true;
                Observe(async () => await CreateTabAsync(BrowserSessionKind.Private));
            }
            else if (e.VirtualKey == 0x09 && _tabs.Tabs.Count > 1 && _tabs.ActiveTab is { } current)
            {
                e.Handled = true;
                var count = _tabs.Tabs.Count;
                var index = (_tabs.Tabs.ToList().FindIndex(tab => tab.Id == current.Id) + (e.Shift ? count - 1 : 1)) % count;
                Observe(async () => await _runtime.ActivateAsync(_tabs.Tabs[index].Id));
            }
            else if (e.VirtualKey == 0x54)
            {
                e.Handled = true;
                Observe(async () => await CreateTabAsync());
            }
            else if (e.VirtualKey == 0x57 && _tabs.ActiveTab is { } active)
            {
                e.Handled = true;
                Observe(async () => await CloseTabAsync(active.Id));
            }
            else if (e.VirtualKey == 0x4C || e.VirtualKey == 0x45)
            {
                e.Handled = true;
                _window.FocusAddress();
            }
            else if (e.VirtualKey == 0x44)
            {
                e.Handled = true;
                ToggleBookmark();
            }
            else if (e.VirtualKey == 0x48)
            {
                e.Handled = true;
                OpenPanel("history");
            }
            else if (e.VirtualKey == 0x52)
            {
                e.Handled = true;
                _runtime.Reload();
            }
            else if (e.VirtualKey >= 0x31 && e.VirtualKey <= 0x39)
            {
                var index = e.VirtualKey - 0x31;
                if (index < _tabs.Tabs.Count)
                {
                    e.Handled = true;
                    Observe(async () => await _runtime.ActivateAsync(_tabs.Tabs[index].Id));
                }
            }
        }
        else if (e.Alt && e.VirtualKey == 0x25)
        {
            e.Handled = true;
            _runtime.GoBack();
        }
        else if (e.Alt && e.VirtualKey == 0x27)
        {
            e.Handled = true;
            _runtime.GoForward();
        }
        else if (e.VirtualKey == 0x74)
        {
            e.Handled = true;
            _runtime.Reload();
        }
    }

    // ---------- Panels ----------

    private void OpenPanel(string mode)
    {
        if (mode != "settings" && mode != "history" && mode != "bookmarks") return;
        if (_panel is null)
        {
            _panel = new InternalPanelWindow(_localization, _window.Handle, _theme);
            _panel.SaveSettingsRequested += (_, submission) => SaveSettings(submission.SearchTemplate, submission.Language, submission.Theme);
            _panel.OpenUrlRequested += (_, url) => Observe(async () => await NavigateAsync(url));
            _panel.RemoveEntryRequested += (_, url) => RemoveEntry(url);
            _panel.ClearHistoryRequested += (_, _) => ClearHistory();
            _panel.CloseRequested += (_, _) => Observe(async () => await ClosePanelAsync());
        }

        _panelMode = mode;
        _panelOwnedByTab = false;
        ShowPanel(mode);
    }

    private void ShowPanel(string mode)
    {
        if (_panel is null) return;
        if (mode == "settings")
        {
            _panel.ShowSettings(_settings?.SearchTemplate ?? string.Empty, _settings?.Language ?? string.Empty,
                _settings?.Theme ?? BrowserThemeMode.System);
        }
        else
        {
            var entries = mode == "history" ? HistoryEntries() : BookmarkEntries();
            _panel.ShowEntries(mode == "history" ? InternalPanelMode.History : InternalPanelMode.Bookmarks, entries);
        }

        RenderState();
    }

    private async Task ClosePanelAsync()
    {
        // The panel is an owned window that took the foreground when it opened. Windows does not
        // always hand the foreground back when an owned window closes, and never does after a modal
        // confirmation was shown in between, so the shell returns focus to the browser itself.
        _window.Activate();

        var activeInternal = ActiveInternalRoute();
        _panelMode = null;
        _panelOwnedByTab = false;
        _panel?.Close();

        // Keyboard focus returns to the page, as it was before the panel opened.
        _observedHost?.Focus();

        // Leaving a browser-owned page with no history returns its tab to the new-tab page.
        if (activeInternal is not null && _tabs?.ActiveTab is { } active && _runtime is not null)
        {
            await _runtime.ResetToNewTabAsync(active.Id);
        }

        RenderState();
    }

    private void SaveSettings(string searchTemplate, string language, BrowserThemeMode theme)
    {
        if (_settingsStore is null) return;
        try
        {
            var updated = new BrowserSettings
            {
                SearchTemplate = NavigationInputParser.ValidateTemplate(searchTemplate),
                Language = BrowserSettings.NormalizeLanguage(language),
                Theme = BrowserSettings.NormalizeTheme(theme),
            };
            _settingsStore.Save(updated);
            _settings = updated;
            _inputParser = new NavigationInputParser(updated.SearchTemplate);
            _localization.LoadLanguage(EffectiveLanguage);
            _window.ApplyLanguage();
            _panel?.ApplyLanguage();
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

    private void RemoveEntry(string url)
    {
        var mode = _panelMode;
        if (mode == "history")
        {
            if (_historyStore?.Remove(url) == true) _historyStore.Save();
        }
        else if (mode == "bookmarks")
        {
            _bookmarkStore?.Remove(url);
        }

        RefreshPanel();
    }

    private void ClearHistory()
    {
        _historyStore?.Clear();
        _historyStore?.Save();
        RefreshPanel();
    }

    private void RefreshPanel()
    {
        if (_panel is null || _panelMode is null) return;
        ShowPanel(_panelMode);
    }

    private List<PanelEntry> HistoryEntries() =>
        _historyStore?.GetAll()
            .Select(item => new PanelEntry(item.Title, item.Url))
            .ToList() ?? new List<PanelEntry>();

    private List<PanelEntry> BookmarkEntries() =>
        _bookmarkStore?.GetAll()
            .Select(item => new PanelEntry(item.Title, item.Url))
            .ToList() ?? new List<PanelEntry>();

    /// <summary>The internal route the active tab is on, or null.</summary>
    private string? ActiveInternalRoute()
    {
        if (_tabs?.ActiveTab is not { Url: { Length: > 0 } url }) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && NavigationInputParser.IsInternalPage(uri)
            ? uri.Host.ToLowerInvariant()
            : null;
    }

    private IReadOnlyList<string> GetAddressSuggestions()
    {
        var suggestions = new List<string>();
        if (_historyStore is not null)
        {
            suggestions.AddRange(_historyStore.GetAll(20).Select(item => item.Url));
        }

        if (_tabs is not null)
        {
            suggestions.AddRange(_tabs.Tabs
                .Where(tab => !string.IsNullOrWhiteSpace(tab.Url))
                .Select(tab => tab.Url));
        }

        return suggestions
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    // ---------- State rendering ----------

    private void RenderState()
    {
        if (_tabs is null) return;
        var active = _tabs.ActiveTab;
        var host = _runtime?.ActiveHost;
        var bookmarked = host is not null && _bookmarkStore?.Contains(host.Source) == true;

        _window.UpdateTabs(_tabs.Tabs, active?.Id, host?.IsLoading == true);
        _window.UpdateNavigationState(host?.CanGoBack ?? false, host?.CanGoForward ?? false);
        _window.SetBookmarked(bookmarked);
        _window.SetAddressText(active?.Url ?? string.Empty);

        var title = active?.Title?.Trim() ?? string.Empty;
        _window.SetWindowTitle(title.Length == 0
            ? _localization.Get("app.name")
            : title + " - " + _localization.Get("app.name"));

        // A browser-owned page is shown by the native panel; an empty tab shows the new-tab page.
        var activeInternal = ActiveInternalRoute();
        if (activeInternal is not null)
        {
            if (_panelMode != activeInternal || _panel is null || !_panel.IsOpen)
            {
                _panelMode = activeInternal;
                _panelOwnedByTab = true;
                ShowPanel(activeInternal);
            }

            _window.SetNewTabPage(false, active?.SessionKind == BrowserSessionKind.Private);
        }
        else
        {
            if (_panelMode is not null && _panelOwnedByTab)
            {
                _panelMode = null;
                _panelOwnedByTab = false;
                _panel?.Close();
            }

            // A tab that holds a URL shows the palette until its content host exists. Showing the
            // new-tab page instead flashes the new-tab text on top of a page that is loading,
            // which is most visible when a session is restored at startup.
            _window.SetNewTabPage(host is null && string.IsNullOrWhiteSpace(active?.Url),
                active?.SessionKind == BrowserSessionKind.Private);
        }

        WriteSmokeState();
    }

    /// <summary>
    /// The chrome surface color as a Win32 COLORREF, so a controller can be created with it and a
    /// page that has not painted yet shows the palette instead of the WebView2 default white.
    /// </summary>
    private System.Drawing.Color PaletteSurfaceColor() => WebView2Color.FromColorref(_window.ContentBackgroundColor);

    private void OnActiveHostChanged(object? sender, EventArgs e)
    {
        if (_observedHost is not null)
        {
            _observedHost.SourceChanged -= OnActiveHostStateChanged;
            _observedHost.TitleChanged -= OnActiveHostStateChanged;
            _observedHost.ProcessFailed -= OnActiveHostFailed;
            _observedHost.SetVisible(false);
        }

        _observedHost = _runtime?.ActiveHost;
        if (_observedHost is not null)
        {
            _observedHost.SourceChanged += OnActiveHostStateChanged;
            _observedHost.TitleChanged += OnActiveHostStateChanged;
            _observedHost.ProcessFailed += OnActiveHostFailed;
            // The palette reaches the surface before it becomes visible, so an unpainted page never
            // shows the WebView2 default white.
            ApplyThemeToHost(_observedHost);
            _observedHost.SetVisible(true);
            _observedHost.SetRasterizationScale(_window.ContentScale);
            ApplyHostBounds(_observedHost);
            _observedHost.Focus();
        }

        RenderState();
    }

    private void OnActiveHostStateChanged(object? sender, EventArgs e) => RenderState();

    private void OnActiveHostFailed(object sender, EventArgs e) => SendNotice(PageStoppedNotice);

    /// <summary>Clears a notice that described a page failure; other notices stay until dismissed.</summary>
    private void ClearPageFailureNotice()
    {
        if (_noticeKey is PageFailedNotice or PageStoppedNotice) SendNotice(null);
    }

    /// <summary>
    /// Keeps the active content surface on the current theme. Native windows follow the shared
    /// theme source themselves; a WebView2 surface is not a native window, so the shell carries it.
    /// </summary>
    private void ApplyThemeToContent()
    {
        if (_observedHost is not null) ApplyThemeToHost(_observedHost);
    }

    /// <summary>
    /// Gives a content surface the chrome's surface color, shown before a page paints, and the
    /// light/dark scheme pages read through prefers-color-scheme.
    /// </summary>
    private void ApplyThemeToHost(ITabHost host)
    {
        host.SetBackgroundColor(_window.ContentBackgroundColor);
        host.SetPrefersDarkColorScheme(_theme.Palette.IsDark);
    }

    private void OnContentBoundsChanged(object? sender, ContentBounds bounds) => ApplyHostBounds(_runtime?.ActiveHost);

    private void ApplyHostBounds(ITabHost? host)
    {
        if (host is null) return;
        var bounds = _window.GetContentBounds();
        host.SetRasterizationScale(_window.ContentScale);
        host.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    // ---------- Runtime events ----------

    private void OnPopupRequested(object? sender, PopupRequestedEventArgs e)
    {
        Uri? target = null;
        var isBlank = string.IsNullOrEmpty(e.TargetUrl) ||
            string.Equals(e.TargetUrl, "about:blank", StringComparison.OrdinalIgnoreCase);
        if (!isBlank &&
            (!Uri.TryCreate(e.TargetUrl, UriKind.Absolute, out target) ||
             (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))) return;

        // A popup becomes an ordinary tab, so the shell keeps one window and one tab model.
        Observe(async () =>
        {
            if (_tabs is null || _runtime is null) return;
            var tab = _tabs.Create(sessionKind: e.SessionKind);
            await _runtime.ActivateAsync(tab.Id, waitForNavigation: false);
            if (target is not null)
            {
                await _runtime.NavigateAsync(tab.Id, target, waitForNavigation: false);
            }

            RenderState();
        });
    }

    private void OnDownloadStarted(object? sender, DownloadStartedEventArgs e) =>
        SendNotice("notice.downloadStarted", Path.GetFileName(e.TargetFilePath));

    private void OnDownloadStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        switch (e.State)
        {
            case BrowserDownloadState.Completed:
                SendNotice("notice.downloadCompleted", Path.GetFileName(e.TargetFilePath));
                break;
            case BrowserDownloadState.Interrupted:
                SendNotice("notice.downloadFailed", (e.InterruptReason ?? "unknown") + ": " + Path.GetFileName(e.TargetFilePath));
                break;
        }
    }

    private void OnPermissionRequested(object? sender, PermissionRequestedEventArgs e)
    {
        if (e.IsAllowed) return;
        SendNotice("notice.permissionDenied", e.SourceUrl);
    }

    private void OnTabNavigationCompleted(object? sender, TabNavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            if (e.Tab.SessionKind == BrowserSessionKind.Normal &&
                Uri.TryCreate(e.Url, UriKind.Absolute, out var navigatedUri) &&
                (navigatedUri.Scheme == Uri.UriSchemeHttp || navigatedUri.Scheme == Uri.UriSchemeHttps))
            {
                _historyStore?.Record(e.Url, e.Title);
            }

            // The page loaded, so a failure notice about it no longer applies. RenderState also
            // replaces the failure window title with the page title.
            if (_tabs?.ActiveTab?.Id == e.Tab.Id) ClearPageFailureNotice();
            RenderState();
        }
        else if (_tabs?.ActiveTab?.Id == e.Tab.Id)
        {
            _window.SetWindowTitle(_localization.Get(PageFailedNotice) + " - " + _localization.Get("app.name"));
            SendNotice(PageFailedNotice, e.Error);
        }
    }

    private void OnPrivateCleanupCompleted(object? sender, SessionCleanupResult result)
    {
        System.Diagnostics.Trace.TraceInformation(
            "Private session cleanup {0}: {1} after {2} attempt(s); {3}",
            result.State,
            result.DataFolder.Length == 0 ? "(no private profile was created)" : result.DataFolder,
            result.Attempts,
            result.Detail ?? "no error");

        if (result.IsComplete)
        {
            _window.SetTimerEnabled(PrivateCleanupTimerId, false);
            return;
        }

        // The data is not gone yet: keep retrying and tell the user instead of claiming success.
        _window.SetTimerEnabled(PrivateCleanupTimerId, true);
        SendNotice(result.State == SessionCleanupState.Failed ? "notice.privateDataFailed" : "notice.privateDataPending");
    }

    private void OnTimerTick(object? sender, int timerId)
    {
        switch (timerId)
        {
            case SleepTimerId:
                Observe(async () =>
                {
                    if (_runtime is not null) await _runtime.SweepSleepAsync();
                });
                break;
            case PrivateCleanupTimerId:
                Observe(async () =>
                {
                    if (_runtime is not null) await _runtime.RetryPendingPrivateCleanupAsync();
                });
                break;
        }
    }

    private const string PageFailedNotice = "notice.navigationFailed";
    private const string PageStoppedNotice = "notice.pageStopped";

    // The notice the window shows, so a page that recovers can clear exactly the notice its
    // failure raised and leave unrelated notices (downloads, permissions) in place.
    private string? _noticeKey;

    private void SendNotice(string? key, string? detail = null)
    {
        _noticeKey = key;
        _window.SetNotice(key, detail);
    }

    // ---------- Shutdown ----------

    private async Task ShutdownAsync()
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _window.SetTimerEnabled(SleepTimerId, false);
        _window.SetTimerEnabled(PrivateCleanupTimerId, false);
        _panel?.Close();

        try
        {
            _historyStore?.Save();
            _bookmarkStore?.Save();
            if (_tabs is not null) _sessionStore?.Save(_tabs.Tabs, _tabs.ActiveTab?.Id);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            TraceError("Browser data could not be saved", exception);
        }

        try
        {
            if (_runtime is not null) await _runtime.ShutdownAsync();
        }
        catch (Exception exception)
        {
            TraceError("Browser hosts did not close cleanly", exception);
        }

        if (_runtime?.PendingPrivateCleanup is { } pendingCleanup)
        {
            System.Diagnostics.Trace.TraceError(
                "Private session data is still present after shutdown: {0} ({1})",
                pendingCleanup.DataFolder,
                pendingCleanup.Detail ?? "no error");
        }

        if (_observedHost is not null)
        {
            _observedHost.SourceChanged -= OnActiveHostStateChanged;
            _observedHost.TitleChanged -= OnActiveHostStateChanged;
            _observedHost.ProcessFailed -= OnActiveHostFailed;
            _observedHost = null;
        }

        _window.CompleteShutdown();
    }

    public void Dispose()
    {
        _panel?.Dispose();
        _window.Dispose();
    }

    // ---------- Helpers ----------

    private string EffectiveLanguage
    {
        get
        {
            var configured = _settings?.Language ?? string.Empty;
            if (configured.Length > 0) return Localization.Normalize(configured);
            return Localization.SystemLanguage();
        }
    }

    private static string ResolveDataRoot()
    {
        var overridePath = Environment.GetEnvironmentVariable("BREZZA_DATA_DIR");
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brezza")
            : Path.GetFullPath(overridePath);
    }

    /// <summary>
    /// Runs an asynchronous browser command started from a native event. The task is never
    /// discarded silently: every failure is traced and reported as a notice.
    /// </summary>
    private void Observe(Func<Task> action)
    {
        _ = ObserveAsync(action);
    }

    private async Task ObserveAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            TraceError("A browser command failed", exception);
            SendNotice("notice.operationFailed", exception.Message);
        }
    }

    private static void TraceError(string message, Exception exception) =>
        System.Diagnostics.Trace.TraceError(message + ": {0}", exception);

    private static void TraceWarning(string message, Exception exception) =>
        System.Diagnostics.Trace.TraceWarning(message + ": {0}", exception.Message);

    private void WriteSmokeState()
    {
#if DEBUG
        NativeSmokeState.Write(_window, _tabs, _runtime, _panelMode);
#endif
    }
}
