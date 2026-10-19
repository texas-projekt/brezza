using System;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, ITabHost> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ITabHost?>> _creating = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _restoring = new(StringComparer.Ordinal);
    private readonly object _restoringGate = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastActivatedUtc = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _navigationGeneration = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ulong> _latestNavigationId = new(StringComparer.Ordinal);
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

    /// <summary>A page in any tab requested an external URI scheme the browser refused.</summary>
    public event EventHandler<ExternalSchemeBlockedEventArgs>? ExternalSchemeBlocked;

    /// <summary>A download started in any tab.</summary>
    public event EventHandler<DownloadStartedEventArgs>? DownloadStarted;

    /// <summary>A download was refused by the browser-wide quota in any tab.</summary>
    public event EventHandler<DownloadBlockedEventArgs>? DownloadBlocked;

    /// <summary>A download changed state in any tab.</summary>
    public event EventHandler<DownloadStatusChangedEventArgs>? DownloadStatusChanged;

    public event EventHandler<DownloadProgressChangedEventArgs>? DownloadProgressChanged;

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
            _hosts.TryRemove(tabId, out _);
            host.Dispose();
        }
        else if (creating is not null)
        {
            ITabHost? created = null;
            try
            {
                created = await creating.ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                // A tab whose host could not be created still has to close. Letting the creation
                // failure escape here would leave the tab stuck in Closing and skip activating the
                // next tab.
                System.Diagnostics.Trace.TraceWarning(
                    "A tab host failed while the tab was closing: {0}", exception.Message);
            }

            if (created is not null)
            {
                _hosts.TryRemove(tabId, out _);
                created.Dispose();
            }
        }

        var next = _registry.Close(tabId);
        _latestNavigationId.TryRemove(tabId, out _);
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
        // A tab that just navigated is working, even when it is not the active tab; the sleep sweep
        // must not release its host as if it had been idle since the last activation.
        _lastActivatedUtc[tabId] = DateTime.UtcNow;
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

    /// <summary>Whether a tab currently has a controller that can report document state.</summary>
    public bool HasContent(string tabId) =>
        !_disposed && _hosts.TryGetValue(tabId, out var host) && host.HasContent;

    /// <summary>
    /// Pauses a download the browser started. A download belongs to the tab that started it, so the
    /// command is routed to the host that owns it, whichever tab is active. Returns false when no
    /// live host has that download.
    /// </summary>
    public bool PauseDownload(string targetFilePath) => ActOnDownload(targetFilePath, host => host.TryPauseDownload(targetFilePath));

    /// <summary>Resumes a paused download. Returns false when no live host has that download.</summary>
    public bool ResumeDownload(string targetFilePath) => ActOnDownload(targetFilePath, host => host.TryResumeDownload(targetFilePath));

    /// <summary>Cancels a download and drops its partial file. Returns false when no live host has it.</summary>
    public bool CancelDownload(string targetFilePath) => ActOnDownload(targetFilePath, host => host.TryCancelDownload(targetFilePath));

    private bool ActOnDownload(string targetFilePath, Func<ITabHost, bool> action)
    {
        if (_disposed || string.IsNullOrEmpty(targetFilePath)) return false;
        foreach (var host in _hosts.Values)
        {
            if (action(host)) return true;
        }

        return false;
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
        _hosts.TryRemove(tabId, out _);
        _lastActivatedUtc.TryRemove(tabId, out _);
        _latestNavigationId.TryRemove(tabId, out _);
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
            .Select(id => (Id: id, HasTime: _lastActivatedUtc.TryGetValue(id, out var last), Last: last))
            .Where(item => item.HasTime && item.Last <= cutoff)
            .OrderBy(item => item.Last)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Id)
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
        var hosts = _hosts.Values.ToArray();
        _hosts.Clear();
        foreach (var host in hosts) host.Dispose();
        var creating = _creating.Values.ToArray();
        foreach (var task in creating)
        {
            try { await task.ConfigureAwait(true); }
            catch (Exception) { }
        }
        foreach (var host in _hosts.Values.ToArray()) host.Dispose();
        _hosts.Clear();
        _creating.Clear();
        lock (_restoringGate) _restoring.Clear();
        _lastActivatedUtc.Clear();
        _navigationGeneration.Clear();
        _latestNavigationId.Clear();
        ActiveHost = null;
        ActiveHostChanged?.Invoke(this, EventArgs.Empty);
        await CleanupPrivateSessionAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Cleans private profile folders left by a previous process before any private tab exists.
    /// The factory decides which folders are safe to remove (an owner that is still alive is kept).
    /// </summary>
    public async Task<SessionCleanupResult?> CleanupOrphanedPrivateDataAsync()
    {
        if (_disposed || _registry.HasSession(BrowserSessionKind.Private)) return null;
        return await CleanupPrivateSessionAsync().ConfigureAwait(true);
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

        var completion = new TaskCompletionSource<ITabHost?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Two callers may race on the same tab; only the winner starts a creation, and both await
        // the same completion.
        if (!_creating.TryAdd(tab.Id, completion.Task))
        {
            return _creating.TryGetValue(tab.Id, out var existing)
                ? existing
                : completion.Task;
        }

        _registry.Update(tab.Id, state: TabRuntimeState.Creating);
        _ = CreateHostAsync(tab, completion);
        return completion.Task;
    }

    private async Task RestoreUrlAsync(BrowserTab tab, ITabHost host, Uri uri)
    {
        Task? pending;
        Task? navigation = null;
        lock (_restoringGate)
        {
            if (!_restoring.TryGetValue(tab.Id, out pending))
            {
                navigation = host.NavigateAsync(uri);
                _restoring[tab.Id] = navigation;
            }
        }

        if (pending is not null)
        {
            await pending.ConfigureAwait(true);
            return;
        }

        try
        {
            await navigation!.ConfigureAwait(true);
        }
        catch (Exception exception) when (
            (_disposed || !_registry.Contains(tab.Id)) &&
            (exception is ObjectDisposedException || exception is TaskCanceledException))
        {
            return;
        }
        finally
        {
            lock (_restoringGate) _restoring.Remove(tab.Id);
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

    private int NextGeneration(string tabId) =>
        _navigationGeneration.AddOrUpdate(tabId, 1, (_, current) => current + 1);

    private void RecordNavigation(string tabId, ulong navigationId)
    {
        if (navigationId == 0) return;
        _latestNavigationId.AddOrUpdate(tabId, navigationId, (_, current) => Math.Max(current, navigationId));
    }

    private bool IsStaleNavigationFailure(string tabId, ulong navigationId) =>
        navigationId != 0 &&
        _latestNavigationId.TryGetValue(tabId, out var latest) &&
        navigationId < latest;

    private bool IsCurrentGeneration(string tabId, int generation) =>
        _navigationGeneration.TryGetValue(tabId, out var current) && current == generation;

    /// <summary>
    /// Whether an unsuccessful navigation ended without the page failing: a newer navigation
    /// replaced it (a reload, a new address, back/forward, or the page navigating itself while it
    /// was loading), a download took it over, or it was cancelled. The newer navigation reports
    /// its own result, and a download reports its own outcome, so neither is shown as a failure.
    /// </summary>
    private static bool IsNotAFailure(TabNavigationResultEventArgs result) =>
        result.IsSuperseded ||
        result.IsReplacedByDownload ||
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

            if (!_hosts.TryAdd(tab.Id, host))
            {
                // A concurrent close/creation already owns this tab; keep the live host authoritative.
                host.Dispose();
                completion.TrySetResult(null);
                return;
            }

            host.SourceChanged += (_, _) => _registry.Update(tab.Id, url: host.Source);
            host.TitleChanged += (_, _) => _registry.Update(tab.Id, title: host.Title);
            host.FaviconChanged += (_, _) => _registry.Update(tab.Id, favicon: host.FaviconUri);
            host.ProcessFailed += (_, _) =>
            {
                // The host invalidates its controller and can recreate it on the next navigation;
                // the tab is failed until a later success proves it recovered.
                if (!_disposed && _registry.Contains(tab.Id)) _registry.Update(tab.Id, state: TabRuntimeState.Failed);
            };
            // A load that was replaced or cancelled also ends unsuccessfully. Remember whether the
            // last completion was one, so it does not mark the tab as failed.
            var lastCompletionNotAFailure = false;
            host.NavigationFailed += (_, args) =>
            {
                // An old failure can arrive after the newer navigation already succeeded. The
                // runtime's navigation id tells the two apart; the completion flag covers sources
                // that report a reason but no id.
                if (lastCompletionNotAFailure) return;
                if (IsStaleNavigationFailure(tab.Id, args.NavigationId)) return;
                _registry.Update(tab.Id, state: TabRuntimeState.Failed);
            };
            host.PopupRequested += (_, args) =>
            {
                if (!_disposed && _registry.Contains(tab.Id))
                {
                    PopupRequested?.Invoke(this, new PopupRequestedEventArgs(
                        args.TargetUrl, tab.SessionKind, args.IsUserInitiated, tab.Id));
                }
            };
            host.ExternalSchemeBlocked += (_, args) => ExternalSchemeBlocked?.Invoke(this, args);
            host.DownloadStarted += (_, args) => DownloadStarted?.Invoke(this, new DownloadStartedEventArgs(
                args.SourceUrl, args.SuggestedFileName, args.MimeType, args.TargetFilePath, tab.SessionKind));
            host.DownloadBlocked += (_, args) => DownloadBlocked?.Invoke(this, args);
            host.DownloadStatusChanged += (_, args) => DownloadStatusChanged?.Invoke(this, args);
            host.DownloadProgressChanged += (_, args) => DownloadProgressChanged?.Invoke(this, args);
            host.PermissionRequested += (_, args) => PermissionRequested?.Invoke(this, args);
            host.NavigationCompleted += (_, result) =>
            {
                RecordNavigation(tab.Id, result.NavigationId);
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
            _creating.TryRemove(tab.Id, out _);
        }
    }
}
