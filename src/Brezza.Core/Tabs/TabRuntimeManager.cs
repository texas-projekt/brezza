using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Brezza.Core.Abstractions;

namespace Brezza.Core.Tabs;

public sealed class TabRuntimeManager
{
    private readonly TabRegistry _registry;
    private readonly ITabHostFactory _factory;
    private readonly Dictionary<string, ITabHost> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<ITabHost?>> _creating = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _restoring = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastActivatedUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _navigationGeneration = new(StringComparer.Ordinal);
    private SessionCleanupResult? _pendingPrivateCleanup;
    private readonly SemaphoreSlim _privateCleanupGate = new(1, 1);
    private const int MaximumLiveHosts = 8;
    private static readonly TimeSpan DefaultInactiveSleepAfter = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _inactiveSleepAfter;
    private bool _disposed;

    public TabRuntimeManager(TabRegistry registry, ITabHostFactory factory, TimeSpan? inactiveSleepAfter = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _inactiveSleepAfter = inactiveSleepAfter ?? DefaultInactiveSleepAfter;
    }

    public ITabHost? ActiveHost { get; private set; }

    public event EventHandler? ActiveHostChanged;

    public event EventHandler<TabNavigationCompletedEventArgs>? NavigationCompleted;

    public event EventHandler<BrowserTab>? RuntimeChanged;

    /// <summary>Raised after every private-session cleanup attempt, successful or not.</summary>
    public event EventHandler<SessionCleanupResult>? PrivateCleanupCompleted;

    /// <summary>A page in any tab asked the browser to open a new window.</summary>
    public event EventHandler<PopupRequestedEventArgs>? PopupRequested;

    /// <summary>A download started in any tab.</summary>
    public event EventHandler<DownloadStartedEventArgs>? DownloadStarted;

    /// <summary>A download changed state in any tab.</summary>
    public event EventHandler<DownloadStatusChangedEventArgs>? DownloadStatusChanged;

    /// <summary>A page in any tab asked for a capability.</summary>
    public event EventHandler<PermissionRequestedEventArgs>? PermissionRequested;

    /// <summary>The private-session cleanup that has not verified removal yet, if any.</summary>
    public SessionCleanupResult? PendingPrivateCleanup => _pendingPrivateCleanup is { IsComplete: false } ? _pendingPrivateCleanup : null;

    public async Task ActivateAsync(string tabId, bool waitForNavigation = true)
    {
        if (_disposed) return;
        var tab = _registry.Activate(tabId);
        _lastActivatedUtc[tabId] = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(tab.Url))
        {
            ActiveHost = null;
            _registry.Update(tabId, state: TabRuntimeState.Active);
            ActiveHostChanged?.Invoke(this, EventArgs.Empty);
            RuntimeChanged?.Invoke(this, tab);
            await SweepSleepAsync().ConfigureAwait(true);
            return;
        }

        var host = await EnsureHostAsync(tab).ConfigureAwait(true);
        if (host is null ||
            !_registry.Contains(tabId) ||
            !ReferenceEquals(_registry.ActiveTab, tab) ||
            tab.State == TabRuntimeState.Closing)
        {
            return;
        }

        ActiveHost = host;
        _registry.Update(tabId, state: TabRuntimeState.Active);
        ActiveHostChanged?.Invoke(this, EventArgs.Empty);
        RuntimeChanged?.Invoke(this, tab);
        await SweepSleepAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(tab.Url) &&
            Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) &&
            !SourcesMatch(host.Source, uri))
        {
            var restoration = RestoreUrlAsync(tab, host, uri);
            if (waitForNavigation) await restoration.ConfigureAwait(true);
            else ObserveNavigation(tab, restoration, NextGeneration(tabId));
        }
    }

    public async Task CloseAsync(string tabId)
    {
        if (_disposed) return;
        var wasActive = _registry.ActiveTab?.Id == tabId;
        var sessionKind = _registry.Find(tabId).SessionKind;
        if (wasActive)
        {
            ActiveHost = null;
            ActiveHostChanged?.Invoke(this, EventArgs.Empty);
        }

        var creating = _creating.TryGetValue(tabId, out var pending) ? pending : null;
        _registry.Update(tabId, state: TabRuntimeState.Closing);
        if (_hosts.TryGetValue(tabId, out var host))
        {
            _hosts.Remove(tabId);
            host.Dispose();
        }
        else if (creating is not null)
        {
            var created = await creating.ConfigureAwait(true);
            if (created is not null)
            {
                _hosts.Remove(tabId);
                created.Dispose();
            }
        }

        var next = _registry.Close(tabId);
        if (wasActive)
        {
            if (next is not null) await ActivateAsync(next.Id).ConfigureAwait(true);
        }

        if (sessionKind == BrowserSessionKind.Private && !_registry.HasSession(BrowserSessionKind.Private))
        {
            await CleanupPrivateSessionAsync().ConfigureAwait(true);
        }
    }

    public async Task NavigateAsync(string tabId, Uri uri, bool waitForNavigation = true)
    {
        if (_disposed) return;
        var host = await EnsureHostAsync(_registry.Find(tabId)).ConfigureAwait(true);
        if (host is null) return;
        if (_registry.ActiveTab?.Id == tabId && !ReferenceEquals(ActiveHost, host))
        {
            ActiveHost = host;
            ActiveHostChanged?.Invoke(this, EventArgs.Empty);
        }

        var navigation = host.NavigateAsync(uri);
        var generation = NextGeneration(tabId);
        if (waitForNavigation) await navigation.ConfigureAwait(true);
        else ObserveNavigation(_registry.Find(tabId), navigation, generation);
    }

    public void GoBack() => ActiveHost?.GoBack();

    public void GoForward() => ActiveHost?.GoForward();

    public void Reload() => ActiveHost?.Reload();

    /// <summary>
    /// A tab's favicon image, or null when the tab has no live host (a new, internal, or sleeping
    /// tab) or its document has no favicon. The image is fetched by the tab's own host, so the
    /// request stays inside that page's profile and network stack.
    /// </summary>
    public Task<Stream?> GetFaviconAsync(string tabId)
    {
        if (_disposed || !_registry.Contains(tabId)) return Task.FromResult<Stream?>(null);
        return _hosts.TryGetValue(tabId, out var host)
            ? host.GetFaviconAsync()
            : Task.FromResult<Stream?>(null);
    }

    /// <summary>
    /// Releases an inactive tab host, but only when the host is eligible: not loading, not playing
    /// audio, and not showing a full-screen element. Returns the outcome so the caller can report why
    /// a tab was kept alive.
    /// </summary>
    public TabSleepOutcome SleepAsync(string tabId)
    {
        if (_disposed) return TabSleepOutcome.NoHost;
        if (!_registry.Contains(tabId)) return TabSleepOutcome.UnknownTab;
        if (_registry.ActiveTab?.Id == tabId) return TabSleepOutcome.ActiveTab;
        if (!_hosts.TryGetValue(tabId, out var host)) return TabSleepOutcome.NoHost;

        var decision = TabSleepPolicy.Evaluate(TabHostSignals.FromHost(host), isActiveTab: false);
        if (decision != TabSleepOutcome.Released) return decision;

        _registry.Update(tabId, state: TabRuntimeState.Sleeping);
        _hosts.Remove(tabId);
        _lastActivatedUtc.Remove(tabId);
        host.Dispose();
        RuntimeChanged?.Invoke(this, _registry.Find(tabId));
        return TabSleepOutcome.Released;
    }

    public Task SweepSleepAsync()
    {
        if (_disposed || _hosts.Count <= MaximumLiveHosts) return Task.CompletedTask;

        var cutoff = DateTime.UtcNow - _inactiveSleepAfter;
        var candidates = _hosts.Keys
            .Where(id => _registry.ActiveTab?.Id != id)
            .Where(id => _lastActivatedUtc.TryGetValue(id, out var last) && last <= cutoff)
            .OrderBy(id => _lastActivatedUtc[id])
            .ToArray();

        // Ineligible candidates are skipped instead of counting against the host budget.
        foreach (var tabId in candidates)
        {
            if (_hosts.Count <= MaximumLiveHosts) break;
            SleepAsync(tabId);
        }

        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var host in _hosts.Values) host.Dispose();
        _hosts.Clear();
        var creating = _creating.Values.ToArray();
        foreach (var task in creating)
        {
            try { await task.ConfigureAwait(true); }
            catch (Exception) { }
        }
        foreach (var host in _hosts.Values) host.Dispose();
        _hosts.Clear();
        _creating.Clear();
        _restoring.Clear();
        _lastActivatedUtc.Clear();
        _navigationGeneration.Clear();
        ActiveHost = null;
        ActiveHostChanged?.Invoke(this, EventArgs.Empty);
        await CleanupPrivateSessionAsync().ConfigureAwait(true);
    }

    /// <summary>Retries a private-session cleanup that did not verify removal yet.</summary>
    public async Task<SessionCleanupResult?> RetryPendingPrivateCleanupAsync()
    {
        if (_disposed || _registry.HasSession(BrowserSessionKind.Private) ||
            _privateCleanupGate.CurrentCount == 0 ||
            _pendingPrivateCleanup is null || _pendingPrivateCleanup.IsComplete) return null;
        return await CleanupPrivateSessionAsync().ConfigureAwait(true);
    }

    private async Task<SessionCleanupResult> CleanupPrivateSessionAsync()
    {
        await _privateCleanupGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!_disposed && _registry.HasSession(BrowserSessionKind.Private))
            {
                return _pendingPrivateCleanup ??
                    new SessionCleanupResult(BrowserSessionKind.Private, SessionCleanupState.Completed, string.Empty, 0, null);
            }

            var result = await _factory.CleanupSessionAsync(BrowserSessionKind.Private).ConfigureAwait(true);
            _pendingPrivateCleanup = result;
            PrivateCleanupCompleted?.Invoke(this, result);
            return result;
        }
        finally
        {
            _privateCleanupGate.Release();
        }
    }

    private Task<ITabHost?> EnsureHostAsync(BrowserTab tab)
    {
        if (_disposed) return Task.FromResult<ITabHost?>(null);
        if (_hosts.TryGetValue(tab.Id, out var host)) return Task.FromResult<ITabHost?>(host);
        if (_creating.TryGetValue(tab.Id, out var creating)) return creating;

        _registry.Update(tab.Id, state: TabRuntimeState.Creating);
        var completion = new TaskCompletionSource<ITabHost?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _creating.Add(tab.Id, completion.Task);
        _ = CreateHostAsync(tab, completion);
        return completion.Task;
    }

    private async Task RestoreUrlAsync(BrowserTab tab, ITabHost host, Uri uri)
    {
        if (_restoring.TryGetValue(tab.Id, out var pending))
        {
            await pending.ConfigureAwait(true);
            return;
        }

        var navigation = host.NavigateAsync(uri);
        _restoring[tab.Id] = navigation;
        try
        {
            await navigation.ConfigureAwait(true);
        }
        catch (Exception exception) when (
            (_disposed || !_registry.Contains(tab.Id)) &&
            (exception is ObjectDisposedException || exception is TaskCanceledException))
        {
            return;
        }
        finally
        {
            _restoring.Remove(tab.Id);
        }
    }

    private void ObserveNavigation(BrowserTab tab, Task navigation, int generation)
    {
        _ = ObserveNavigationAsync(tab, navigation, generation);
    }

    private async Task ObserveNavigationAsync(BrowserTab tab, Task navigation, int generation)
    {
        try
        {
            await navigation.ConfigureAwait(true);
        }
        catch (Exception exception) when (
            (_disposed || !_registry.Contains(tab.Id)) &&
            (exception is ObjectDisposedException || exception is TaskCanceledException))
        {
        }
        catch
        {
            if (IsCurrentGeneration(tab.Id, generation)) _registry.Update(tab.Id, state: TabRuntimeState.Failed);
        }
    }

    private int NextGeneration(string tabId)
    {
        var next = _navigationGeneration.TryGetValue(tabId, out var current) ? current + 1 : 1;
        _navigationGeneration[tabId] = next;
        return next;
    }

    private bool IsCurrentGeneration(string tabId, int generation) =>
        _navigationGeneration.TryGetValue(tabId, out var current) && current == generation;

    /// <summary>
    /// Whether an unsuccessful navigation ended without the page failing: a newer navigation
    /// replaced it (a reload, a new address, back/forward, or the page navigating itself while it
    /// was loading), or it was cancelled. The newer navigation reports its own result, so neither is
    /// shown as a failure.
    /// </summary>
    private static bool IsNotAFailure(TabNavigationResultEventArgs result) =>
        result.IsSuperseded ||
        string.Equals(result.WebErrorStatus, "OperationCanceled", StringComparison.Ordinal);

    private static bool SourcesMatch(string source, Uri uri) =>
        string.Equals(source, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private async Task CreateHostAsync(BrowserTab tab, TaskCompletionSource<ITabHost?> completion)
    {
        try
        {
            var host = await _factory.CreateAsync(tab.Id, tab.SessionKind).ConfigureAwait(true);
            if (_disposed || !_registry.Contains(tab.Id))
            {
                host.Dispose();
                completion.TrySetResult(null);
                return;
            }

            _hosts.Add(tab.Id, host);
            host.SourceChanged += (_, _) => _registry.Update(tab.Id, url: host.Source);
            host.TitleChanged += (_, _) => _registry.Update(tab.Id, title: host.Title);
            host.FaviconChanged += (_, _) => _registry.Update(tab.Id, favicon: host.FaviconUri);
            // A load that was replaced or cancelled also ends unsuccessfully. Remember whether the
            // last completion was one, so it does not mark the tab as failed.
            var lastCompletionNotAFailure = false;
            host.NavigationFailed += (_, _) =>
            {
                if (!lastCompletionNotAFailure) _registry.Update(tab.Id, state: TabRuntimeState.Failed);
            };
            host.PopupRequested += (_, args) =>
            {
                if (!_disposed && _registry.Contains(tab.Id))
                {
                    PopupRequested?.Invoke(this, new PopupRequestedEventArgs(args.TargetUrl, tab.SessionKind));
                }
            };
            host.DownloadStarted += (_, args) => DownloadStarted?.Invoke(this, args);
            host.DownloadStatusChanged += (_, args) => DownloadStatusChanged?.Invoke(this, args);
            host.PermissionRequested += (_, args) => PermissionRequested?.Invoke(this, args);
            host.NavigationCompleted += (_, result) =>
            {
                lastCompletionNotAFailure = !result.IsSuccess && IsNotAFailure(result);
                if (lastCompletionNotAFailure) return;

                if (result.IsSuccess)
                {
                    // A page that loads after a failure (a reload, or any later navigation) ends the
                    // failure; the tab is live again.
                    var recovered = _registry.Contains(tab.Id) && _registry.Find(tab.Id).State == TabRuntimeState.Failed;
                    // The favicon is synced here as well as on its own event: an icon that arrived
                    // around this completion is not lost when no further change follows it.
                    _registry.Update(tab.Id, url: host.Source, title: host.Title,
                        favicon: host.FaviconUri,
                        state: recovered ? TabRuntimeState.Active : null);
                }

                NavigationCompleted?.Invoke(
                    this,
                    new TabNavigationCompletedEventArgs(tab, host.Source, host.Title, result.IsSuccess, result.WebErrorStatus));
            };
            completion.TrySetResult(host);
        }
        catch (Exception exception)
        {
            _registry.Update(tab.Id, state: TabRuntimeState.Failed);
            completion.TrySetException(exception);
        }
        finally
        {
            _creating.Remove(tab.Id);
        }
    }
}
