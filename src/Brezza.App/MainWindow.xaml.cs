using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Web.Script.Serialization;
using Brezza.Core.Abstractions;
using Brezza.Core.Bridge;
using Brezza.Core.Navigation;
using Brezza.Core.Tabs;
using Brezza.Persistence;
using Brezza.WebView2;
using Microsoft.Web.WebView2.Core;

namespace Brezza.App;

public partial class MainWindow : Window
{
    private NavigationInputParser? _inputParser;
    private SettingsStore? _settingsStore;
    private BrowserSettings? _settings;
    private HistoryStore? _historyStore;
    private BookmarkStore? _bookmarkStore;
    private SessionStore? _sessionStore;
    private WebView2TabHostFactory? _contentHostFactory;
    private TabRegistry? _tabs;
    private TabRuntimeManager? _runtime;
    private BrowserChromeBridge? _chromeBridge;
    private WebView2ChromeHost? _chromeHost;
    private ITabHost? _observedHost;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private Exception? _lastChromeCommandException;
    private readonly SemaphoreSlim _chromeCommandGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _chromeReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureWebView2Runtime();
            var dataRoot = ResolveDataRoot();
            _settingsStore = new SettingsStore(Path.Combine(dataRoot, "settings.xml"));
            _settings = _settingsStore.Load();
            _inputParser = new NavigationInputParser(_settings.SearchTemplate);
            _historyStore = new HistoryStore(Path.Combine(dataRoot, "history.xml"));
            _bookmarkStore = new BookmarkStore(Path.Combine(dataRoot, "bookmarks.xml"));
            _sessionStore = new SessionStore(Path.Combine(dataRoot, "session.xml"));

            _chromeHost = new WebView2ChromeHost();
            _chromeBridge = new BrowserChromeBridge(_chromeHost);
            _chromeBridge.CommandReceived += OnChromeCommandReceived;
            ChromeContainer.Content = _chromeHost;
            await _chromeHost.InitializeAsync(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BrowserUi"),
                Path.Combine(dataRoot, "ChromeProfile"));

            _tabs = new TabRegistry();
            _tabs.Changed += OnTabsChanged;
            _contentHostFactory = new WebView2TabHostFactory(
                Path.Combine(dataRoot, "WebView2"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BrowserUi"));
            _runtime = new TabRuntimeManager(_tabs, _contentHostFactory);
            _runtime.ActiveHostChanged += OnActiveHostChanged;
            _runtime.NavigationCompleted += OnTabNavigationCompleted;

            var session = _sessionStore.Load();
            foreach (var tab in session.Tabs!) _tabs.Restore(tab.Id, tab.Url, tab.Title);
            if (_tabs.Tabs.Count == 0) _tabs.Create();

            await _runtime.ActivateAsync(session.ActiveTabId ?? _tabs.Tabs[0].Id);
            SendState();
#if DEBUG
            var smokeMode = Environment.GetEnvironmentVariable("BREZZA_UI_SMOKE");
            if (!string.IsNullOrWhiteSpace(smokeMode)) await RunDebugSmokeAsync(smokeMode);
#endif
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowHostError("Microsoft Edge WebView2 Runtime is required.");
        }
        catch (Exception exception)
        {
            ShowHostError(exception.Message);
        }
    }

    private async void OnChromeCommandReceived(object? sender, BrowserUiCommand command)
    {
        var serialize = command.Type != "internalReady" && command.Type != "focusAddress";
        try
        {
            if (serialize) await _chromeCommandGate.WaitAsync().ConfigureAwait(true);
            switch (command.Type)
            {
                case "ready":
                    _chromeReady.TrySetResult(true);
                    SendState();
                    Send(new { version = 1, type = "focusAddress" });
                    break;
                case "navigate":
                    await NavigateAsync(command.GetString("text"));
                    break;
                case "openSettings":
                    await OpenInternalPageAsync("settings");
                    break;
                case "openHistory":
                    await OpenInternalPageAsync("history");
                    break;
                case "openBookmarks":
                    await OpenInternalPageAsync("bookmarks");
                    break;
                case "internalReady":
                    SendInternalState(command.GetString("page"));
                    break;
                case "closeInternalPage":
                    await CloseInternalPageAsync();
                    break;
                case "focusAddress":
                    Send(new { version = 1, type = "focusAddress" });
                    break;
                case "newTab":
                    await CreateTabAsync();
                    break;
                case "newPrivateTab":
                    await CreateTabAsync(BrowserSessionKind.Private);
                    break;
                case "activateTab":
                    await _runtime!.ActivateAsync(command.GetString("tabId"));
                    SendState();
                    break;
                case "closeTab":
                    await CloseTabAsync(command.GetString("tabId"));
                    break;
                case "back":
                    _runtime?.GoBack();
                    break;
                case "forward":
                    _runtime?.GoForward();
                    break;
                case "reload":
                    _runtime?.Reload();
                    break;
                case "toggleBookmark":
                    ToggleBookmark();
                    break;
                case "saveSettings":
                    SaveSettings(command.GetString("searchTemplate"), command.GetString("language"));
                    break;
                case "removeBookmark":
                    RemoveBookmark(command.GetString("url"));
                    break;
                case "layout":
                    ChromeRow.Height = new GridLength(command.GetInt32("height"));
                    ApplyChromeLayout();
                    break;
            }
        }
        catch (Exception exception)
        {
            _lastChromeCommandException = exception;
            SendNotice("notice.operationFailed", exception.Message);
        }
        finally
        {
            if (serialize) _chromeCommandGate.Release();
        }
    }

    private async Task NavigateAsync(string input)
    {
        if (_tabs?.ActiveTab is null || _runtime is null || _inputParser is null) return;
        var target = _inputParser.Parse(input);
        if (target.Kind == NavigationTargetKind.Empty || target.Uri is null) return;

        Title = "Loading - Brezza";
        var logicalUrl = NavigationInputParser.IsInternalPage(target.Uri)
            ? NavigationInputParser.GetCanonicalInternalUrl(target.Uri)
            : target.Uri.AbsoluteUri;
        _tabs.SetUrl(_tabs.ActiveTab.Id, logicalUrl);
        await _runtime.NavigateAsync(_tabs.ActiveTab.Id, target.Uri, waitForNavigation: false);
        SendState();
        (_observedHost as WebView2TabHost)?.View.Focus();
    }

    private async Task CreateTabAsync(BrowserSessionKind? requestedKind = null)
    {
        if (_tabs is null || _runtime is null) return;
        var sessionKind = requestedKind ?? _tabs.ActiveTab?.SessionKind ?? BrowserSessionKind.Normal;
        var tab = _tabs.Create(sessionKind: sessionKind);
        await _runtime.ActivateAsync(tab.Id, waitForNavigation: false);
        SendState();
    }

    private void OnShortcutPressed(object? sender, BrowserShortcutEventArgs e)
    {
        if (e.Control)
        {
            if (e.VirtualKey == 0x4E && e.Shift)
            {
                e.Handled = true;
                QueueShortcut(() => CreateTabAsync(BrowserSessionKind.Private));
            }
            else if (e.VirtualKey == 0x09 && _tabs is { Tabs.Count: > 1 } && _runtime is not null && _tabs.ActiveTab is { } current)
            {
                e.Handled = true;
                var count = _tabs.Tabs.Count;
                var index = (_tabs.Tabs.ToList().FindIndex(tab => tab.Id == current.Id) + (e.Shift ? count - 1 : 1)) % count;
                var tabId = _tabs.Tabs[index].Id;
                QueueShortcut(() => _runtime.ActivateAsync(tabId));
            }
            else if (e.VirtualKey == 0x54)
            {
                e.Handled = true;
                QueueShortcut(() => CreateTabAsync());
            }
            else if (e.VirtualKey == 0x57 && _tabs?.ActiveTab is { } active)
            {
                e.Handled = true;
                QueueShortcut(() => CloseTabAsync(active.Id));
            }
            else if (e.VirtualKey == 0x4C)
            {
                e.Handled = true;
                Send(new { version = 1, type = "focusAddress" });
            }
            else if (e.VirtualKey == 0x44)
            {
                e.Handled = true;
                ToggleBookmark();
            }
            else if (e.VirtualKey >= 0x31 && e.VirtualKey <= 0x39 && _tabs is not null && _runtime is not null)
            {
                var index = e.VirtualKey - 0x31;
                if (index < _tabs.Tabs.Count)
                {
                    e.Handled = true;
                    var tabId = _tabs.Tabs[index].Id;
                    QueueShortcut(() => _runtime.ActivateAsync(tabId));
                }
            }
        }
        else if (e.Alt && e.VirtualKey == 0x25)
        {
            e.Handled = true;
            _runtime?.GoBack();
        }
        else if (e.Alt && e.VirtualKey == 0x27)
        {
            e.Handled = true;
            _runtime?.GoForward();
        }
        else if (e.VirtualKey == 0x74)
        {
            e.Handled = true;
            _runtime?.Reload();
        }
    }

    private void QueueShortcut(Func<Task> action)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await action(); }
            catch (Exception exception) { SendNotice("notice.operationFailed", exception.Message); }
        }));
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

        SendState();
    }

    private void ToggleBookmark()
    {
        if (_runtime?.ActiveHost is not { } host || _bookmarkStore is null) return;
        if (_bookmarkStore.Contains(host.Source)) _bookmarkStore.Remove(host.Source);
        else _bookmarkStore.Add(host.Source, host.Title);
        SendState();
    }

    private void RemoveBookmark(string url)
    {
        if (_bookmarkStore is null) return;
        _bookmarkStore.Remove(url);
        SendInternalState("bookmarks");
        SendState();
    }

    private void SaveSettings(string searchTemplate, string language)
    {
        if (_settingsStore is null) return;
        try
        {
            var updated = new BrowserSettings
            {
                SearchTemplate = NavigationInputParser.ValidateTemplate(searchTemplate),
                Language = BrowserSettings.NormalizeLanguage(language),
            };
            _settingsStore.Save(updated);
            _settings = updated;
            _inputParser = new NavigationInputParser(updated.SearchTemplate);
            Send(new { version = 1, type = "settingsSaved", language = EffectiveLanguage });
            SendState();
        }
        catch (ArgumentException)
        {
            Send(new { version = 1, type = "settingsError" });
        }
        catch (IOException)
        {
            Send(new { version = 1, type = "settingsError" });
        }
    }

    private void SendState()
    {
        if (_tabs is null) return;
        var active = _tabs.ActiveTab;
        var host = _runtime?.ActiveHost;
        var state = new
        {
            tabs = _tabs.Tabs.Select(tab => new
            {
                id = tab.Id,
                title = tab.Title,
                url = tab.Url,
                isPrivate = tab.SessionKind == BrowserSessionKind.Private,
            }).ToArray(),
            activeTabId = active?.Id,
            isPrivate = active?.SessionKind == BrowserSessionKind.Private,
            language = EffectiveLanguage,
            canGoBack = host?.CanGoBack ?? false,
            canGoForward = host?.CanGoForward ?? false,
            bookmarked = host is not null && _bookmarkStore?.Contains(host.Source) == true,
        };
        Send(new { version = 1, type = "state", state });
    }

    private async Task OpenInternalPageAsync(string page)
    {
        if (_tabs?.ActiveTab is null || _runtime is null) return;
        var route = new Uri("brezza://" + page);
        _tabs.SetUrl(_tabs.ActiveTab.Id, NavigationInputParser.GetCanonicalInternalUrl(route));
        await _runtime.NavigateAsync(_tabs.ActiveTab.Id, route, waitForNavigation: false);
        SendState();
    }

    private async Task CloseInternalPageAsync()
    {
        if (_runtime?.ActiveHost is not { } host) return;
        if (host.CanGoBack) _runtime.GoBack();
        else if (_tabs?.ActiveTab is { } active)
        {
            // A restored internal page may have no history entry; return it to the canonical new-tab page.
            var route = new Uri(NavigationInputParser.NewTabUrl);
            _tabs.SetUrl(active.Id, NavigationInputParser.NewTabUrl);
            await _runtime.NavigateAsync(active.Id, route, waitForNavigation: false);
            SendState();
        }
    }

    private void SendInternalState(string page)
    {
        if (!string.Equals(page, "newtab", StringComparison.Ordinal) &&
            !string.Equals(page, "settings", StringComparison.Ordinal) &&
            !string.Equals(page, "history", StringComparison.Ordinal) &&
            !string.Equals(page, "bookmarks", StringComparison.Ordinal)) return;

        if (_runtime?.ActiveHost is not { } host ||
            !string.Equals(host.Source, "brezza://" + page, StringComparison.OrdinalIgnoreCase)) return;

        if (string.Equals(page, "newtab", StringComparison.Ordinal))
        {
            SendToInternalPage(new
            {
                version = 1,
                type = "internalState",
                page,
                language = EffectiveLanguage,
                isPrivate = _tabs?.ActiveTab?.SessionKind == BrowserSessionKind.Private,
            });
        }
        else if (string.Equals(page, "settings", StringComparison.Ordinal))
        {
            SendToInternalPage(new
            {
                version = 1,
                type = "internalState",
                page,
                language = EffectiveLanguage,
                configuredLanguage = _settings?.Language ?? string.Empty,
                searchTemplate = _settings?.SearchTemplate ?? string.Empty,
                isPrivate = _tabs?.ActiveTab?.SessionKind == BrowserSessionKind.Private,
            });
        }
        else if (string.Equals(page, "history", StringComparison.Ordinal))
        {
            var items = _historyStore?.GetAll()
                .Select(item => new { title = item.Title, url = item.Url })
                .ToArray() ?? Array.Empty<object>();
            SendToInternalPage(new
            {
                version = 1,
                type = "internalState",
                page,
                language = EffectiveLanguage,
                isPrivate = _tabs?.ActiveTab?.SessionKind == BrowserSessionKind.Private,
                items,
            });
        }
        else
        {
            var items = _bookmarkStore?.GetAll()
                .Select(item => new { title = item.Title, url = item.Url })
                .ToArray() ?? Array.Empty<object>();
            SendToInternalPage(new
            {
                version = 1,
                type = "internalState",
                page,
                language = EffectiveLanguage,
                isPrivate = _tabs?.ActiveTab?.SessionKind == BrowserSessionKind.Private,
                items,
            });
        }
    }

    private void SendToInternalPage(object message)
    {
        if (_observedHost is null) return;
        _observedHost.PostMessage(new JavaScriptSerializer().Serialize(message));
    }

    private void SendNotice(string key, string? detail = null) => Send(new { version = 1, type = "notice", key, detail });

    private void Send(object message) => _chromeBridge?.Send(message);

    private void OnTabsChanged(object? sender, EventArgs e) => SendState();

    private void OnActiveHostChanged(object? sender, EventArgs e)
    {
        if (_observedHost is not null)
        {
            _observedHost.SourceChanged -= OnActiveHostChangedEvent;
            _observedHost.TitleChanged -= OnActiveHostChangedEvent;
            _observedHost.ProcessFailed -= OnActiveHostFailed;
            _observedHost.ShortcutPressed -= OnShortcutPressed;
            _observedHost.InternalMessageReceived -= OnInternalMessageReceived;
        }

        _observedHost = _runtime?.ActiveHost;
        if (_observedHost is not null)
        {
            _observedHost.SourceChanged += OnActiveHostChangedEvent;
            _observedHost.TitleChanged += OnActiveHostChangedEvent;
            _observedHost.ProcessFailed += OnActiveHostFailed;
            _observedHost.ShortcutPressed += OnShortcutPressed;
            _observedHost.InternalMessageReceived += OnInternalMessageReceived;
            PageHost.Content = (_observedHost as WebView2TabHost)?.View;
        }
        else
        {
            PageHost.Content = null;
        }

        ApplyChromeLayout();

        var activeTitle = _observedHost?.Title?.Trim() ?? string.Empty;
        Title = activeTitle.Length == 0 ? "Brezza" : activeTitle + " - Brezza";
        SendState();
    }

    /// <summary>
    /// The chrome and the page are separate HWND surfaces. The content surface owns every tab page,
    /// including the trusted new-tab page.
    /// </summary>
    private void ApplyChromeLayout()
    {
        Grid.SetRowSpan(ChromeContainer, 1);
        PageHost.Visibility = Visibility.Visible;
    }

    private void OnInternalMessageReceived(object? sender, Brezza.Core.Abstractions.ChromeMessageEventArgs e)
    {
        _chromeBridge?.AcceptMessageJson(e.Json, fromInternalPage: true);
    }

    private void OnActiveHostChangedEvent(object? sender, EventArgs e)
    {
        var activeTitle = _observedHost?.Title?.Trim() ?? string.Empty;
        Title = activeTitle.Length == 0 ? "Brezza" : activeTitle + " - Brezza";
        SendState();
    }

    private void OnActiveHostFailed(object? sender, EventArgs e) => SendNotice("notice.pageStopped");

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
            SendState();
        }
        else if (_tabs?.ActiveTab?.Id == e.Tab.Id)
        {
            Title = "Navigation failed - Brezza";
            SendNotice("notice.navigationFailed", e.Error);
        }
    }

    private void ShowHostError(string message)
    {
        Title = "Brezza - Startup error";
        StartupErrorPlaceholder.Text = message;
        StartupErrorPlaceholder.Visibility = Visibility.Visible;
        PageHost.Visibility = Visibility.Collapsed;
    }

#if DEBUG
    private async Task RunDebugSmokeAsync(string mode)
    {
        var resultPath = Environment.GetEnvironmentVariable("BREZZA_SMOKE_RESULT");
        try
        {
            await _chromeReady.Task;
            var pageUrl = Environment.GetEnvironmentVariable("BREZZA_SMOKE_PAGE") ?? string.Empty;
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
            {
                throw new InvalidOperationException("Smoke page URL is missing.");
            }

            if (mode == "restore")
            {
                await WaitForAsync(() =>
                    _runtime?.ActiveHost is { } host &&
                    string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(host.Title, "Brezza Smoke Page", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
                WriteSmokeResult(resultPath, "PASS: session restored through WebView2 chrome.");
                Close();
                return;
            }

            await WaitForAsync(() =>
                string.Equals(_runtime?.ActiveHost?.Source, NavigationInputParser.NewTabUrl, StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(5));
            var newTabPage = await ExecuteActivePageScriptAsync(
                "document.getElementById('newtab-focus') ? 'newtab-ok' : 'newtab-missing'");
            if (newTabPage.IndexOf("newtab-ok", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("The new-tab route did not load as a trusted internal page: " + newTabPage);
            }

            var focusedControl = await _chromeHost!.ExecuteScriptAsync(
                "document.dispatchEvent(new KeyboardEvent('keydown',{key:'l',ctrlKey:true,bubbles:true,cancelable:true}));document.activeElement.id");
            if (focusedControl.IndexOf("OmniBox", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Ctrl+L did not focus the chrome address field.");
            }
            var accessibleChrome = await _chromeHost.ExecuteScriptAsync(
                "(() => { const ids=['NewTabButton','NewPrivateTabButton','back','forward','reload','bookmark','settings']; " +
                "const named=ids.every(id=>{const e=document.getElementById(id);return e&&(e.getAttribute('aria-label')||e.title)}); " +
                "return !!(named&&document.getElementById('OmniBox').getAttribute('aria-label')); })()");
            if (!string.Equals(accessibleChrome, "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Browser chrome controls are missing accessible names or labels.");
            }
            var requestedLanguage = Environment.GetEnvironmentVariable("BREZZA_SMOKE_LANGUAGE");
            if (!string.IsNullOrWhiteSpace(requestedLanguage))
            {
                var localizedLabel = await _chromeHost.ExecuteScriptAsync(
                    "JSON.stringify({lang:document.documentElement.lang,privateTab:document.getElementById('NewPrivateTabButton').getAttribute('aria-label')})");
                if (localizedLabel.IndexOf("zh-Hans", StringComparison.OrdinalIgnoreCase) < 0 ||
                    localizedLabel.IndexOf("新建隐私标签页", StringComparison.Ordinal) < 0)
                {
                    throw new InvalidOperationException("Requested locale was not applied: " + localizedLabel);
                }
            }

            await _chromeHost.ExecuteScriptAsync("document.getElementById('settings').click();true;");
            await WaitForAsync(async () =>
            {
                if (!string.Equals(_runtime?.ActiveHost?.Source, "brezza://settings", StringComparison.OrdinalIgnoreCase)) return false;
                var value = await ExecuteActivePageScriptAsync("document.readyState==='complete'&&document.getElementById('settings-form')!==null");
                return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }, TimeSpan.FromSeconds(5));
            await ExecuteActivePageScriptAsync("document.getElementById('close-page').click();true;");
            try
            {
                await WaitForAsync(() => !string.Equals(_runtime?.ActiveHost?.Source, "brezza://settings", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    "Closing settings did not leave the page. Tab=" + _tabs?.ActiveTab?.Url +
                    "; canGoBack=" + _runtime?.ActiveHost?.CanGoBack + "; exception=" + _lastChromeCommandException);
            }

            await _chromeHost.ExecuteScriptAsync("document.getElementById('history').click();true;");
            await WaitForAsync(async () =>
            {
                if (!string.Equals(_runtime?.ActiveHost?.Source, "brezza://history", StringComparison.OrdinalIgnoreCase)) return false;
                var value = await ExecuteActivePageScriptAsync("document.readyState==='complete'&&document.getElementById('history-list')!==null");
                return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }, TimeSpan.FromSeconds(5));
            await ExecuteActivePageScriptAsync("document.getElementById('close-page').click();true;");
            await WaitForAsync(() => !string.Equals(_runtime?.ActiveHost?.Source, "brezza://history", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5));

            await _chromeHost.ExecuteScriptAsync("document.getElementById('bookmarks').click();true;");
            await WaitForAsync(async () =>
            {
                if (!string.Equals(_runtime?.ActiveHost?.Source, "brezza://bookmarks", StringComparison.OrdinalIgnoreCase)) return false;
                var value = await ExecuteActivePageScriptAsync("document.readyState==='complete'&&document.getElementById('history-list')!==null");
                return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }, TimeSpan.FromSeconds(5));
            await ExecuteActivePageScriptAsync("document.getElementById('close-page').click();true;");
            await WaitForAsync(() => !string.Equals(_runtime?.ActiveHost?.Source, "brezza://bookmarks", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5));

            var firstNavigation = WaitForNavigationAsync(pageUri.AbsoluteUri, expectedSuccess: true);
            await NavigateThroughChromeAsync(pageUri.AbsoluteUri);
            await firstNavigation;

            await _chromeHost.ExecuteScriptAsync("document.getElementById('NewTabButton').click();true;");
            await WaitForAsync(() => _tabs?.Tabs.Count == 2 && _tabs.ActiveTab?.Id == "tab-2", TimeSpan.FromSeconds(5));
            var secondNavigation = WaitForNavigationAsync(pageUri.AbsoluteUri, expectedSuccess: true);
            await NavigateThroughChromeAsync(pageUri.AbsoluteUri);
            await secondNavigation;

            await _chromeHost.ExecuteScriptAsync("document.getElementById('TabSelect_tab-1').click();true;");
            await WaitForAsync(() => _tabs?.ActiveTab?.Id == "tab-1", TimeSpan.FromSeconds(5));
            await _chromeHost.ExecuteScriptAsync(
                "document.getElementById('TabSelect_tab-1').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true,cancelable:true}));true;");
            await WaitForAsync(() => _tabs?.ActiveTab?.Id == "tab-2", TimeSpan.FromSeconds(5));

            var missingUri = new Uri(Path.Combine(Path.GetDirectoryName(pageUri.LocalPath)!, "missing.html"));
            var failedNavigation = WaitForNavigationAsync(missingUri.AbsoluteUri, expectedSuccess: false);
            await NavigateThroughChromeAsync(missingUri.AbsoluteUri);
            await failedNavigation;
            await _chromeHost.ExecuteScriptAsync("document.getElementById('TabClose_tab-2').click();true;");
            await WaitForAsync(() => _tabs?.Tabs.Count == 1 && _tabs.ActiveTab?.Id == "tab-1", TimeSpan.FromSeconds(5));

            await _chromeHost.ExecuteScriptAsync("document.getElementById('NewPrivateTabButton').click();true;");
            try
            {
                await WaitForAsync(() => _tabs?.Tabs.Count == 2 && _tabs.ActiveTab?.SessionKind == BrowserSessionKind.Private, TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                var notice = await _chromeHost.ExecuteScriptAsync("document.getElementById('notice').textContent");
                throw new InvalidOperationException(
                    "Private tab command was not applied. Tabs=" + _tabs?.Tabs.Count +
                    "; notice=" + notice + "; exception=" + _lastChromeCommandException);
            }
            var tabSemantics = await _chromeHost.ExecuteScriptAsync(
                "(() => { const t=document.getElementById('TabSelect_tab-3'); const c=document.getElementById('TabClose_tab-3'); " +
                "return !!(t&&t.getAttribute('role')==='tab'&&t.getAttribute('aria-label')&&" +
                "c&&c.tagName==='BUTTON'&&c.getAttribute('aria-label')); })()");
            if (!string.Equals(tabSemantics, "true", StringComparison.OrdinalIgnoreCase))
            {
                var details = await _chromeHost.ExecuteScriptAsync(
                    "JSON.stringify({tabs:[...document.querySelectorAll('.tab')].map(t=>({id:t.id,role:t.getAttribute('role'),label:t.getAttribute('aria-label')})),close:document.getElementById('TabClose_tab-3')?.outerHTML})");
                throw new InvalidOperationException("Tab strip is missing accessible tab/close semantics: " + details);
            }
            var privateNavigation = WaitForNavigationAsync(pageUri.AbsoluteUri, expectedSuccess: true);
            await NavigateThroughChromeAsync(pageUri.AbsoluteUri);
            await privateNavigation;
            var privateProfile = _contentHostFactory?.PrivateUserDataFolder ??
                throw new InvalidOperationException("Private profile folder was not created.");
            await WaitForAsync(() => Directory.Exists(privateProfile), TimeSpan.FromSeconds(5));

            await _chromeHost.ExecuteScriptAsync("document.getElementById('NewPrivateTabButton').click();true;");
            await WaitForAsync(() => _tabs?.Tabs.Count == 3 && _tabs.ActiveTab?.SessionKind == BrowserSessionKind.Private, TimeSpan.FromSeconds(5));
            if (!string.Equals(_contentHostFactory?.PrivateUserDataFolder, privateProfile, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Tabs in one private session did not share its isolated profile.");
            }
            var secondPrivateNavigation = WaitForNavigationAsync(pageUri.AbsoluteUri, expectedSuccess: true);
            await NavigateThroughChromeAsync(pageUri.AbsoluteUri);
            await secondPrivateNavigation;

            await _chromeHost.ExecuteScriptAsync("document.getElementById('TabClose_tab-4').click();true;");
            await WaitForAsync(() => _tabs?.Tabs.Count == 2 && Directory.Exists(privateProfile), TimeSpan.FromSeconds(5));
            await _chromeHost.ExecuteScriptAsync("document.getElementById('TabClose_tab-3').click();true;");
            await WaitForAsync(() => _tabs?.Tabs.Count == 1 && !Directory.Exists(privateProfile), TimeSpan.FromSeconds(8));

            WriteSmokeResult(resultPath, "PASS: chrome bridge, normal/private tabs, private cleanup, failure state, and session save.");
            Close();
        }
        catch (Exception exception)
        {
            WriteSmokeResult(resultPath, "FAIL: " + exception);
            Close();
        }
    }

    private async Task NavigateThroughChromeAsync(string value)
    {
        var json = new JavaScriptSerializer().Serialize(value);
        await _chromeHost!.ExecuteScriptAsync(
            "window.chrome.webview.postMessage(JSON.stringify({version:1,type:'navigate',args:{text:" + json + "}}));true;");
    }

    private async Task WaitForNavigationAsync(string uri, bool expectedSuccess)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedResult = "no completion event";
        EventHandler<TabNavigationCompletedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (string.Equals(args.Url, uri, StringComparison.OrdinalIgnoreCase))
            {
                observedResult = "success=" + args.IsSuccess + ", error=" + args.Error;
                if (args.IsSuccess == expectedSuccess) completion.TrySetResult(true);
            }
        };
        _runtime!.NavigationCompleted += handler;
        try
        {
            try
            {
                await WaitForAsync(() => completion.Task.IsCompleted, TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException)
            {
                var notice = await _chromeHost!.ExecuteScriptAsync("document.getElementById('notice').textContent");
                throw new TimeoutException("Navigation event mismatch for " + uri + ": " + observedResult +
                    "; tab=" + _tabs?.ActiveTab?.Url + "; host=" + _runtime.ActiveHost?.Source +
                    "; title=" + Title + "; notice=" + notice);
            }
        }
        finally
        {
            _runtime.NavigationCompleted -= handler;
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for browser state.");
    }

    private static async Task WaitForAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for browser UI state.");
    }

    private async Task<string> ExecuteActivePageScriptAsync(string script)
    {
        if (_runtime?.ActiveHost is not WebView2TabHost host)
        {
            throw new InvalidOperationException("The active tab is not a WebView2 content page.");
        }

        return await host.ExecuteScriptAsync(script);
    }

    private static void WriteSmokeResult(string? path, string result)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        File.WriteAllText(path, result);
    }
#endif

    private static string ResolveDataRoot()
    {
        var overridePath = Environment.GetEnvironmentVariable("BREZZA_DATA_DIR");
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brezza.Next")
            : Path.GetFullPath(overridePath);
    }

    private string EffectiveLanguage
    {
        get
        {
            var configured = _settings?.Language ?? string.Empty;
            if (configured.Length > 0) return BrowserSettings.NormalizeLanguage(configured);
            return CultureInfo.InstalledUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hans"
                : "en";
        }
    }

    private static void EnsureWebView2Runtime()
    {
        var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
        if (string.IsNullOrWhiteSpace(version)) throw new WebView2RuntimeNotFoundException();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        Hide();

        try
        {
            _historyStore?.Save();
            _bookmarkStore?.Save();
            if (_tabs is not null) _sessionStore?.Save(_tabs.Tabs, _tabs.ActiveTab?.Id);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError("Browser data could not be saved: {0}", exception);
        }

        try
        {
            if (_runtime is not null) await _runtime.ShutdownAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Browser hosts did not close cleanly: {0}", exception);
        }

        _chromeHost?.Dispose();
        _runtime = null;
        _chromeHost = null;
        _observedHost = null;
        _shutdownComplete = true;
        Close();
    }
}
