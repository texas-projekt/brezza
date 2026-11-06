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
    private DownloadStore? _downloadStore;
    private DownloadRegistry? _downloads;
    private readonly PopupRequestLimiter _popupLimiter = new();
    private readonly PermissionNoticeLimiter _permissionNotices = new();
    private DateTime _lastDownloadProgressUiUtc = DateTime.MinValue;

    /// <summary>How often the downloads panel is refreshed while a download reports progress.</summary>
    private static readonly TimeSpan DownloadProgressUiInterval = TimeSpan.FromMilliseconds(200);
    private WebView2TabHostFactory? _hostFactory;
    private IBrowserDataManager? _dataManager;
    private TabRegistry? _tabs;
    private TabRuntimeManager? _runtime;
    private TabFaviconCache? _favicons;
    private ITabHost? _observedHost;
    private InternalPanelWindow? _panel;

    private string? _panelMode;
    private bool _shutdownStarted;

    public BrowserShell()
    {
        _localization = new Localization(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BrowserUi"));
        _window = new MainWindow(_localization, _theme, tabId => _favicons?.Image(tabId));
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
        Observe(InitializeAsync);
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
        _window.SessionEndingRequested += (_, _) => SaveBrowserData();
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
            _downloadStore = new DownloadStore(Path.Combine(_dataRoot, "downloads.xml"));

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

    private async Task InitializeAsync()
    {
        if (_shutdownStarted) return;
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
            if (_shutdownStarted) return;
            _hostFactory = new WebView2TabHostFactory(
                Path.Combine(_dataRoot, "WebView2"),
                _window.Handle,
                // The saved folder is normalized on load; an empty value keeps the factory's own
                // default of the user's Downloads folder.
                _settings?.DownloadsFolder ?? string.Empty,
                PaletteSurfaceColor());

            // The factory owns the profiles, so it is also the browser's data manager: the shell
            // never reaches a WebView2 object to read or clear profile data.
            _dataManager = _hostFactory;

            _tabs = new TabRegistry();
            _tabs.Changed += (_, _) => RenderState();
            _runtime = new TabRuntimeManager(_tabs, _hostFactory);
            _runtime.ActiveHostChanged += OnActiveHostChanged;
            _runtime.NavigationCompleted += OnTabNavigationCompleted;
            _runtime.PrivateCleanupCompleted += OnPrivateCleanupCompleted;
            _runtime.PopupRequested += OnPopupRequested;
            _runtime.ExternalSchemeBlocked += OnExternalSchemeBlocked;
            _runtime.DownloadStarted += OnDownloadStarted;
            _runtime.DownloadBlocked += OnDownloadBlocked;
            _runtime.DownloadStatusChanged += OnDownloadStatusChanged;
            _runtime.DownloadProgressChanged += OnDownloadProgressChanged;
            _runtime.PermissionRequested += OnPermissionRequested;

            if (_shutdownStarted) return;

            // Private profiles are not persisted, but a crash can leave a folder behind. The
            // factory keeps folders whose owner process is still alive, so this is safe with a
            // second instance.
            await _runtime.CleanupOrphanedPrivateDataAsync();
            if (_shutdownStarted) return;

            // The download list is browser state Core owns; the store only persists it. Restoring
            // the stored records keeps the list across sessions, and a private download is never
            // among them because the store never writes one.
            _downloads = new DownloadRegistry();
            foreach (var download in _downloadStore!.GetAll()) _downloads.Restore(download);

            // The favicon images are fetched by each tab's own host, so the shell only decodes and
            // caches what Core already tracks as the tab's favicon URI.
            _favicons = new TabFaviconCache(
                tabId => _runtime is { } runtime ? runtime.GetFaviconAsync(tabId) : Task.FromResult<Stream?>(null),
                tabId => _runtime?.HasContent(tabId) == true,
                () => _window.InvalidateTabStrip());

            // The saved session is only opened when the user chose that startup behavior; the
            // default is a single new-tab page. The session is still saved at shutdown, so turning
            // the option on later restores what was open when the browser last closed.
            var session = _settings?.StartupBehavior == BrowserStartupBehavior.RestoreSession
                ? _sessionStore!.Load()
                : new SessionStore.BrowserSession();
            foreach (var tab in session.Tabs!) _tabs.Restore(tab.Id, tab.Url, tab.Title);
            if (_tabs.Tabs.Count == 0) _tabs.Create();

            await _runtime.ActivateAsync(session.ActiveTabId ?? _tabs.Tabs[0].Id);
            if (_shutdownStarted) return;
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
    {
        if (_tabs?.ActiveTab is null || _runtime is null || _inputParser is null) return;

        // Every panel is a toolbar view, so navigating closes it.
        ClosePanel();

        var target = _inputParser.Parse(input);
        if (target.Kind == NavigationTargetKind.Empty || target.Uri is null) return;

        _tabs.SetUrl(_tabs.ActiveTab.Id, target.Uri.AbsoluteUri);
        await _runtime.NavigateAsync(_tabs.ActiveTab.Id, target.Uri, waitForNavigation: false);
        RenderState();
        _runtime.ActiveHost?.Focus();
    }

    private async Task CreateTabAsync(BrowserSessionKind? requestedKind = null)
    {
        if (_tabs is null || _runtime is null) return;
        ClosePanel();
        var sessionKind = requestedKind ?? _tabs.ActiveTab?.SessionKind ?? BrowserSessionKind.Normal;
        var tab = _tabs.Create(sessionKind: sessionKind);
        await _runtime.ActivateAsync(tab.Id, waitForNavigation: false);
        RenderState();
    }

    private async Task ActivateTabAsync(string tabId)
    {
        if (_runtime is null) return;
        ClosePanel();
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
            else if (e.VirtualKey == 0x4A)
            {
                // Ctrl+J is the standard shortcut for the downloads list.
                e.Handled = true;
                OpenPanel("downloads");
            }
            else if (e.VirtualKey == 0x2E && e.Shift)
            {
                // Ctrl+Shift+Delete is the standard shortcut for clearing browsing data.
                e.Handled = true;
                OpenPanel("data");
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

    // ---------- State rendering ----------

    private void RenderState()
    {
        if (_tabs is null) return;
        var active = _tabs.ActiveTab;
        var host = _runtime?.ActiveHost;
        var bookmarked = host is not null && _bookmarkStore?.Contains(host.Source) == true;

        _favicons?.Sync(_tabs.Tabs);
        _window.UpdateTabs(_tabs.Tabs, active?.Id, host?.IsLoading == true);
        _window.UpdateNavigationState(host?.CanGoBack ?? false, host?.CanGoForward ?? false);
        _window.SetBookmarked(bookmarked);
        _window.SetAddressText(active?.Url ?? string.Empty);

        var title = active?.Title?.Trim() ?? string.Empty;
        _window.SetWindowTitle(title.Length == 0
            ? _localization.Get("app.name")
            : title + " - " + _localization.Get("app.name"));

        // A tab that holds a URL shows the palette until its content host exists. Showing the
        // new-tab page instead flashes the new-tab text on top of a page that is loading,
        // which is most visible when a session is restored at startup.
        _window.SetNewTabPage(host is null && string.IsNullOrWhiteSpace(active?.Url),
            active?.SessionKind == BrowserSessionKind.Private);

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

    private void OnExternalSchemeBlocked(object? sender, ExternalSchemeBlockedEventArgs e) =>
        SendNotice("notice.externalSchemeBlocked", e.Uri);

    private void OnPopupRequested(object? sender, PopupRequestedEventArgs e)
    {
        if (!_popupLimiter.TryAcquire(e.TabId, e.IsUserInitiated, DateTime.UtcNow))
        {
            SendNotice("notice.popupBlocked", e.TargetUrl);
            return;
        }

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

    private void OnDownloadStarted(object? sender, DownloadStartedEventArgs e)
    {
        if (_downloads is not null)
        {
            _downloads.Start(e.SourceUrl, Path.GetFileName(e.TargetFilePath), e.TargetFilePath, e.SessionKind);
            SaveDownloads();
        }

        SendNotice("notice.downloadStarted", Path.GetFileName(e.TargetFilePath));
        RefreshDownloadsPanel();
    }

    private void OnDownloadBlocked(object? sender, DownloadBlockedEventArgs e)
    {
        // The detail names the file and why it was refused; the key alone would leave the user
        // without the one fact that explains the missing download.
        var reason = _localization.Get(e.Reason == DownloadBlockReason.TooLarge
            ? "notice.downloadBlockedTooLarge"
            : "notice.downloadBlockedTooMany");
        SendNotice("notice.downloadBlocked", reason + ": " + e.SuggestedFileName);
    }

    private void OnDownloadStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        switch (e.State)
        {
            case BrowserDownloadState.Completed:
                _downloads?.Complete(e.TargetFilePath, e.TotalBytes);
                SendNotice("notice.downloadCompleted", Path.GetFileName(e.TargetFilePath));
                break;
            case BrowserDownloadState.Interrupted:
                _downloads?.Interrupt(e.TargetFilePath, e.InterruptReason);
                SendNotice("notice.downloadFailed", (e.InterruptReason ?? "unknown") + ": " + Path.GetFileName(e.TargetFilePath));
                break;
            case BrowserDownloadState.Paused:
                // The adapter reports a pause itself: the runtime keeps the download and its bytes.
                _downloads?.Pause(e.TargetFilePath);
                break;
            case BrowserDownloadState.InProgress:
                _downloads?.Resume(e.TargetFilePath);
                break;
        }

        SaveDownloads();
        RefreshDownloadsPanel();
    }

    /// <summary>
    /// Records how much of a download has arrived. The runtime reports progress often, so the
    /// record keeps every value while the panel is refreshed on a fixed cadence: a fast download
    /// cannot flood the message loop with repaints, and the list is never saved for progress,
    /// because only a finished record belongs on the device.
    /// </summary>
    private void OnDownloadProgressChanged(object? sender, DownloadProgressChangedEventArgs e)
    {
        _downloads?.ReportProgress(e.TargetFilePath, e.ReceivedBytes);
        if (DateTime.UtcNow - _lastDownloadProgressUiUtc < DownloadProgressUiInterval) return;
        _lastDownloadProgressUiUtc = DateTime.UtcNow;
        RefreshDownloadsPanel();
    }

    private void PauseDownload(string targetFilePath) => _runtime?.PauseDownload(targetFilePath);

    private void ResumeDownload(string targetFilePath) => _runtime?.ResumeDownload(targetFilePath);

    private void CancelDownload(string targetFilePath) => _runtime?.CancelDownload(targetFilePath);

    private void OnPermissionRequested(object? sender, PermissionRequestedEventArgs e)
    {
        if (e.IsAllowed) return;
        if (!_permissionNotices.ShouldNotify(e.SourceUrl, e.Kind, DateTime.UtcNow)) return;
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
        // A private download is listed while its session is open and is never persisted, so the
        // session ending is also when its entries leave the list.
        if (_downloads?.RemovePrivateSession() > 0) RefreshDownloadsPanel();

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
    private string? _noticeDetail;

    private void SendNotice(string? key, string? detail = null)
    {
        _noticeKey = key;
        _noticeDetail = detail;
        _window.SetNotice(key, detail);
    }

    // ---------- Shutdown ----------

    /// <summary>
    /// Saves the data that must survive a normal close or a Windows session end. Called
    /// synchronously from WM_QUERYENDSESSION, where waiting for the asynchronous shutdown would be
    /// too late.
    /// </summary>
    private void SaveBrowserData()
    {
        try
        {
            _historyStore?.Save();
            _bookmarkStore?.Save();
            if (_tabs is not null) _sessionStore?.Save(_tabs.Tabs, _tabs.ActiveTab?.Id);
        }
        catch (Exception exception)
        {
            // Saving must never block the close or the session end.
            TraceError("Browser data could not be saved", exception);
        }
    }

    private async Task ShutdownAsync()
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _window.SetTimerEnabled(SleepTimerId, false);
        _window.SetTimerEnabled(PrivateCleanupTimerId, false);
        _panel?.Close();

        SaveBrowserData();

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
        _favicons?.Dispose();
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
