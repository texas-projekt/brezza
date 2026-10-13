using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Permissions;
using Brezza.Core.Tabs;

namespace Brezza.Core.Tests.Tabs;

public sealed class TabRuntimeManagerTests
{
    [Fact]
    public async Task Concurrent_activation_shares_one_host_creation()
    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://example.com/");
        var factory = new DelayedFactory();
        var manager = new TabRuntimeManager(registry, factory);

        var first = manager.ActivateAsync(tab.Id);
        var second = manager.ActivateAsync(tab.Id);
        factory.Complete();
        await Task.WhenAll(first, second);

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(TabRuntimeState.Active, tab.State);
        Assert.NotNull(manager.ActiveHost);
    }

    [Fact]
    public async Task Activating_another_tab_switches_the_active_host_and_keeps_the_first()
    {
        var registry = new TabRegistry();
        var first = registry.Create("https://example.com/one");
        var second = registry.Create("https://example.com/two");
        var factory = new DelayedFactory();
        var manager = new TabRuntimeManager(registry, factory);

        var firstActivation = manager.ActivateAsync(first.Id);
        var firstHost = factory.Complete();
        await firstActivation;
        firstHost.SetVisible(true);
        firstHost.SetBounds(0, 40, 800, 600);

        var secondActivation = manager.ActivateAsync(second.Id);
        var secondHost = factory.Complete();
        await secondActivation;

        // The shell shows exactly one surface: the active host changes, the first host survives and
        // reports the view state it was last given.
        Assert.Same(secondHost, manager.ActiveHost);
        Assert.False(ReferenceEquals(firstHost, secondHost));
        Assert.True(firstHost.IsVisible);
        Assert.Equal((0, 40, 800, 600), firstHost.Bounds);
        Assert.False(secondHost.IsVisible);

        // Reactivating the first tab reuses its host instead of creating a third one.
        await manager.ActivateAsync(first.Id);
        Assert.Same(firstHost, manager.ActiveHost);
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task Releasing_a_host_keeps_the_tab_but_removes_its_surface()
    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://example.com/");
        var factory = new DelayedFactory();
        var manager = new TabRuntimeManager(registry, factory);

        var activating = manager.ActivateAsync(tab.Id);
        var host = factory.Complete();
        await activating;

        await manager.ReleaseHostAsync(tab.Id);

        Assert.True(host.IsDisposed);
        Assert.Null(manager.ActiveHost);
        Assert.True(registry.Contains(tab.Id));
        Assert.Equal("https://example.com/", tab.Url);
        Assert.Equal(TabRuntimeState.Active, tab.State);
    }

    [Fact]
    public async Task Releasing_an_unknown_tab_is_a_no_op()
    {
        var registry = new TabRegistry();
        var manager = new TabRuntimeManager(registry, new DelayedFactory());

        await manager.ReleaseHostAsync("tab-404");

        Assert.Empty(registry.Tabs);
        Assert.Null(manager.ActiveHost);
    }
    [Fact]
    public async Task Closing_during_creation_closes_the_new_host_and_removes_it()    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://example.com/");
        var factory = new DelayedFactory();
        var manager = new TabRuntimeManager(registry, factory);

        var activating = manager.ActivateAsync(tab.Id);
        var closing = manager.CloseAsync(tab.Id);
        var host = factory.Complete();
        await Task.WhenAll(activating, closing);

        Assert.False(registry.Contains(tab.Id));
        Assert.True(host.IsDisposed);
        Assert.Null(manager.ActiveHost);
    }

    [Fact]
    public async Task Shutdown_during_creation_closes_the_late_host()
    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://example.com/");
        var factory = new DelayedFactory();
        var manager = new TabRuntimeManager(registry, factory);

        var activating = manager.ActivateAsync(tab.Id);
        var shuttingDown = manager.ShutdownAsync();
        var host = factory.Complete();
        await Task.WhenAll(activating, shuttingDown);

        Assert.True(host.IsDisposed);
        Assert.Null(manager.ActiveHost);
    }

    [Fact]
    public async Task Navigation_completion_is_reported_with_its_tab_even_after_switching()
    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://example.com/");
        var factory = new DelayedFactory();
        var manager = new TabRuntimeManager(registry, factory);
        TabNavigationCompletedEventArgs? reported = null;
        manager.NavigationCompleted += (_, args) => reported = args;

        var activating = manager.ActivateAsync(tab.Id);
        var host = factory.Complete();
        await activating;
        await host.NavigateAsync(new Uri("https://example.com/"));
        host.RaiseNavigationCompleted(true, "Success");

        Assert.NotNull(reported);
        Assert.Equal(tab.Id, reported!.Tab.Id);
        Assert.Equal("https://example.com/", reported.Url);
    }

    [Fact]
    public async Task Closing_last_private_tab_cleans_its_profile()
    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://example.com/", BrowserSessionKind.Private);
        var factory = new DelayedFactory();
        var manager = new TabRuntimeManager(registry, factory);
        await ActivateWithHostAsync(manager, factory, tab.Id);

        await manager.CloseAsync(tab.Id);

        Assert.Equal(BrowserSessionKind.Private, factory.LastSessionKind);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, factory.CleanupCount);
        Assert.True(factory.LastCleanupResult!.IsComplete);
        Assert.Null(manager.PendingPrivateCleanup);
    }

    [Fact]
    public async Task Pending_private_cleanup_is_reported_and_retried_until_complete()
    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://example.com/", BrowserSessionKind.Private);
        var factory = new DelayedFactory { NextCleanupState = SessionCleanupState.Pending };
        var manager = new TabRuntimeManager(registry, factory);
        SessionCleanupResult? reported = null;
        manager.PrivateCleanupCompleted += (_, result) => reported = result;
        await ActivateWithHostAsync(manager, factory, tab.Id);

        await manager.CloseAsync(tab.Id);

        Assert.NotNull(reported);
        Assert.Equal(SessionCleanupState.Pending, reported!.State);
        Assert.Equal("PrivateSessions/fake", reported.DataFolder);
        Assert.NotNull(manager.PendingPrivateCleanup);

        factory.NextCleanupState = SessionCleanupState.Completed;
        var retried = await manager.RetryPendingPrivateCleanupAsync();

        Assert.NotNull(retried);
        Assert.True(retried!.IsComplete);
        Assert.Equal(2, factory.CleanupCount);
        Assert.Null(manager.PendingPrivateCleanup);
    }

    [Fact]
    public async Task Failed_private_cleanup_stays_pending_until_a_later_attempt_succeeds()
    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://example.com/", BrowserSessionKind.Private);
        var factory = new DelayedFactory { NextCleanupState = SessionCleanupState.Failed };
        var manager = new TabRuntimeManager(registry, factory);
        await ActivateWithHostAsync(manager, factory, tab.Id);

        await manager.CloseAsync(tab.Id);

        Assert.Equal(SessionCleanupState.Failed, manager.PendingPrivateCleanup?.State);
        Assert.Equal("The directory is being used by another process.", manager.PendingPrivateCleanup?.Detail);

        factory.NextCleanupState = SessionCleanupState.Completed;
        var retried = await manager.RetryPendingPrivateCleanupAsync();

        Assert.True(retried!.IsComplete);
        Assert.Null(manager.PendingPrivateCleanup);
    }

    [Fact]
    public async Task Cleanup_retry_does_not_remove_a_new_active_private_session()
    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://private.example/", BrowserSessionKind.Private);
        var factory = new DelayedFactory { NextCleanupState = SessionCleanupState.Pending };
        var manager = new TabRuntimeManager(registry, factory);
        await ActivateWithHostAsync(manager, factory, tab.Id);
        await manager.CloseAsync(tab.Id);

        var newTab = registry.Create("https://new-private.example/", BrowserSessionKind.Private);
        var newHost = await ActivateWithHostAsync(manager, factory, newTab.Id);

        Assert.Null(await manager.RetryPendingPrivateCleanupAsync());
        Assert.Equal(1, factory.CleanupCount);
        Assert.NotNull(manager.PendingPrivateCleanup);
        Assert.False(newHost.IsDisposed);

        factory.NextCleanupState = SessionCleanupState.Completed;
        await manager.CloseAsync(newTab.Id);
        Assert.Equal(2, factory.CleanupCount);
        Assert.Null(manager.PendingPrivateCleanup);
    }

    [Fact]
    public async Task Cleanup_timer_does_not_overlap_an_inflight_retry()
    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://private.example/", BrowserSessionKind.Private);
        var factory = new DelayedFactory { NextCleanupState = SessionCleanupState.Pending };
        var manager = new TabRuntimeManager(registry, factory);
        await ActivateWithHostAsync(manager, factory, tab.Id);
        await manager.CloseAsync(tab.Id);

        factory.CleanupCompletion = new TaskCompletionSource<SessionCleanupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = manager.RetryPendingPrivateCleanupAsync();
        Assert.Null(await manager.RetryPendingPrivateCleanupAsync());
        Assert.Equal(2, factory.CleanupCount);

        factory.CleanupCompletion.SetResult(new SessionCleanupResult(BrowserSessionKind.Private, SessionCleanupState.Completed, "PrivateSessions/fake", 1, null));
        Assert.True((await first)!.IsComplete);
        Assert.Null(manager.PendingPrivateCleanup);
    }

    [Fact]
    public async Task Shutdown_reports_a_private_cleanup_that_could_not_remove_the_data()
    {
        var registry = new TabRegistry();
        var tab = registry.Create("https://example.com/", BrowserSessionKind.Private);
        var factory = new DelayedFactory { NextCleanupState = SessionCleanupState.Pending };
        var manager = new TabRuntimeManager(registry, factory);
        await ActivateWithHostAsync(manager, factory, tab.Id);
        await manager.CloseAsync(tab.Id);

        await manager.ShutdownAsync();

        Assert.Equal(2, factory.CleanupCount);
        Assert.NotNull(manager.PendingPrivateCleanup);
    }

    [Fact]
    public async Task Inactive_tab_playing_audio_is_not_released()
    {
        var context = await CreateTwoTabsAsync();
        context.BackgroundHost.IsDocumentPlayingAudio = true;

        Assert.Equal(TabSleepOutcome.PlayingAudio, context.Manager.SleepAsync(context.Background.Id));
        Assert.False(context.BackgroundHost.IsDisposed);
    }

    [Fact]
    public async Task Inactive_tab_that_is_loading_is_not_released()
    {
        var context = await CreateTwoTabsAsync();
        context.BackgroundHost.IsLoading = true;

        Assert.Equal(TabSleepOutcome.Loading, context.Manager.SleepAsync(context.Background.Id));
        Assert.False(context.BackgroundHost.IsDisposed);
    }

    [Fact]
    public async Task Inactive_tab_in_full_screen_is_not_released()
    {
        var context = await CreateTwoTabsAsync();
        context.BackgroundHost.ContainsFullScreenElement = true;

        Assert.Equal(TabSleepOutcome.FullScreen, context.Manager.SleepAsync(context.Background.Id));
        Assert.False(context.BackgroundHost.IsDisposed);
    }

    [Fact]
    public async Task Idle_inactive_tab_is_released()
    {
        var context = await CreateTwoTabsAsync();

        Assert.Equal(TabSleepOutcome.Released, context.Manager.SleepAsync(context.Background.Id));
        Assert.True(context.BackgroundHost.IsDisposed);
        Assert.Equal(TabRuntimeState.Sleeping, context.Background.State);
    }

    [Fact]
    public async Task The_active_tab_is_never_released()
    {
        var context = await CreateTwoTabsAsync();

        Assert.Equal(TabSleepOutcome.ActiveTab, context.Manager.SleepAsync(context.Active.Id));
        Assert.False(context.ActiveHost.IsDisposed);
    }

    [Fact]
    public async Task Sleep_sweep_skips_ineligible_candidates_and_keeps_the_host_budget()
    {
        var registry = new TabRegistry();
        var factory = new DelayedFactory();
        // A zero inactivity window makes every background tab a sweep candidate.
        var manager = new TabRuntimeManager(registry, factory, TimeSpan.Zero);
        var hosts = new List<FakeHost>();
        for (var index = 0; index < 9; index++)
        {
            var tab = registry.Create("https://example" + index + ".com/");
            var host = await ActivateWithHostAsync(manager, factory, tab.Id);
            if (index == 0) host.IsDocumentPlayingAudio = true;
            hosts.Add(host);
        }

        // The sweep ran while the ninth host was created: the audio tab is not a candidate, so the
        // oldest idle background tab was released instead and the budget holds.
        Assert.False(hosts[0].IsDisposed);
        Assert.True(hosts[1].IsDisposed);
        Assert.Equal(TabRuntimeState.Sleeping, registry.Tabs[1].State);
        Assert.Equal(1, hosts.Count(host => host.IsDisposed));

        await manager.SweepSleepAsync();

        Assert.Equal(1, hosts.Count(host => host.IsDisposed));
    }

    [Fact]
    public async Task Default_sleep_sweep_does_not_release_recently_activated_tabs()
    {
        var registry = new TabRegistry();
        var factory = new DelayedFactory();
        var manager = new TabRuntimeManager(registry, factory);
        var hosts = new List<FakeHost>();
        for (var index = 0; index < 9; index++)
        {
            var tab = registry.Create("https://example" + index + ".com/");
            hosts.Add(await ActivateWithHostAsync(manager, factory, tab.Id));
        }

        await manager.SweepSleepAsync();

        Assert.All(hosts, host => Assert.False(host.IsDisposed));
    }

    private sealed class TwoTabs
    {
        public required TabRuntimeManager Manager { get; init; }

        public required BrowserTab Active { get; init; }

        public required BrowserTab Background { get; init; }

        public required FakeHost ActiveHost { get; init; }

        public required FakeHost BackgroundHost { get; init; }
    }

    [Fact]
    public async Task Popup_download_and_permission_events_are_reported_for_any_tab()
    {
        var context = await CreateTwoTabsAsync();
        PopupRequestedEventArgs? popup = null;
        DownloadStartedEventArgs? downloadStarted = null;
        DownloadStatusChangedEventArgs? downloadStatus = null;
        PermissionRequestedEventArgs? permission = null;
        context.Manager.PopupRequested += (_, args) => popup = args;
        context.Manager.DownloadStarted += (_, args) => downloadStarted = args;
        context.Manager.DownloadStatusChanged += (_, args) => downloadStatus = args;
        context.Manager.PermissionRequested += (_, args) => permission = args;

        context.BackgroundHost.RaisePopup("https://popup.example/");
        context.BackgroundHost.RaiseDownloadStarted("https://example.com/file.pdf", "file.pdf", "C:\\Downloads\\file.pdf");
        context.BackgroundHost.RaiseDownloadStatus(BrowserDownloadState.Interrupted, "C:\\Downloads\\file.pdf", "NetworkFailed");
        context.BackgroundHost.RaisePermission(BrowserPermissionKind.Geolocation, "https://background.example/", isAllowed: false);

        Assert.Equal("https://popup.example/", popup?.TargetUrl);
        Assert.Equal("C:\\Downloads\\file.pdf", downloadStarted?.TargetFilePath);
        Assert.Equal(BrowserDownloadState.Interrupted, downloadStatus?.State);
        Assert.Equal("NetworkFailed", downloadStatus?.InterruptReason);
        Assert.Equal(BrowserPermissionKind.Geolocation, permission?.Kind);
        Assert.False(permission!.IsAllowed);
    }

    [Fact]
    public async Task A_popup_without_a_target_is_reported_as_a_blank_window()
    {
        var context = await CreateTwoTabsAsync();
        PopupRequestedEventArgs? popup = null;
        context.Manager.PopupRequested += (_, args) => popup = args;

        context.BackgroundHost.RaisePopup(null);

        Assert.Empty(popup!.TargetUrl);
    }

    [Fact]
    public async Task Background_private_popup_preserves_its_originating_session()
    {
        var registry = new TabRegistry();
        var factory = new DelayedFactory();
        var manager = new TabRuntimeManager(registry, factory);
        var privateTab = registry.Create("https://private.example/", BrowserSessionKind.Private);
        var privateHost = await ActivateWithHostAsync(manager, factory, privateTab.Id);
        var normalTab = registry.Create("https://normal.example/");
        await ActivateWithHostAsync(manager, factory, normalTab.Id);
        PopupRequestedEventArgs? popup = null;
        manager.PopupRequested += (_, args) => popup = args;

        privateHost.RaisePopup("https://popup.example/");

        Assert.Equal(normalTab.Id, registry.ActiveTab!.Id);
        Assert.Equal(BrowserSessionKind.Private, popup!.SessionKind);
    }

    private static async Task<TwoTabs> CreateTwoTabsAsync()
    {
        var registry = new TabRegistry();
        var factory = new DelayedFactory();
        var manager = new TabRuntimeManager(registry, factory);
        var active = registry.Create("https://active.example/");
        var background = registry.Create("https://background.example/");
        var activeHost = await ActivateWithHostAsync(manager, factory, active.Id);
        var backgroundHost = await ActivateWithHostAsync(manager, factory, background.Id);
        await manager.ActivateAsync(active.Id);
        return new TwoTabs
        {
            Manager = manager,
            Active = active,
            Background = background,
            ActiveHost = activeHost,
            BackgroundHost = backgroundHost,
        };
    }

    private static async Task<FakeHost> ActivateWithHostAsync(TabRuntimeManager manager, DelayedFactory factory, string tabId)
    {
        var activating = manager.ActivateAsync(tabId);
        var host = factory.Complete();
        await activating;
        return host;
    }

    private sealed class DelayedFactory : ITabHostFactory
    {
        private readonly Queue<TaskCompletionSource<ITabHost>> _pending = new();

        public int CreateCount { get; private set; }

        public BrowserSessionKind LastSessionKind { get; private set; }

        public int CleanupCount { get; private set; }

        public SessionCleanupResult? LastCleanupResult { get; private set; }

        /// <summary>State the next cleanup attempt reports; used to simulate a held profile directory.</summary>
        public SessionCleanupState NextCleanupState { get; set; } = SessionCleanupState.Completed;

        public TaskCompletionSource<SessionCleanupResult>? CleanupCompletion { get; set; }

        public Task<ITabHost> CreateAsync(string tabId, BrowserSessionKind sessionKind)
        {
            CreateCount++;
            LastSessionKind = sessionKind;
            var completion = new TaskCompletionSource<ITabHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(completion);
            return completion.Task;
        }

        public Task<SessionCleanupResult> CleanupSessionAsync(BrowserSessionKind sessionKind)
        {
            CleanupCount++;
            LastSessionKind = sessionKind;
            var state = NextCleanupState;
            LastCleanupResult = new SessionCleanupResult(
                sessionKind,
                state,
                "PrivateSessions/fake",
                CleanupCount,
                state == SessionCleanupState.Completed ? null : "The directory is being used by another process.");
            return CleanupCompletion?.Task ?? Task.FromResult(LastCleanupResult);
        }

        public FakeHost Complete()
        {
            var host = new FakeHost();
            if (_pending.Count > 0) _pending.Dequeue().TrySetResult(host);
            return host;
        }
    }

    private sealed class FakeHost : ITabHost
    {
        public string Source { get; private set; } = string.Empty;

        public string Title { get; private set; } = "New Tab";

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public bool IsLoading { get; set; }

        public bool IsDocumentPlayingAudio { get; set; }

        public bool ContainsFullScreenElement { get; set; }

        public bool IsDisposed { get; private set; }

        public bool IsVisible { get; private set; }

        public double RasterizationScale { get; private set; }

        public (int X, int Y, int Width, int Height) Bounds { get; private set; }

        public event EventHandler? SourceChanged;

        public event EventHandler? TitleChanged;

        public event EventHandler? ProcessFailed;

        public event EventHandler? NavigationFailed;

        public event EventHandler<TabNavigationResultEventArgs>? NavigationCompleted;

        public event EventHandler<PopupRequestedEventArgs>? PopupRequested;

        public event EventHandler<DownloadStartedEventArgs>? DownloadStarted;

        public event EventHandler<DownloadStatusChangedEventArgs>? DownloadStatusChanged;

        public event EventHandler<PermissionRequestedEventArgs>? PermissionRequested;

        public Task NavigateAsync(Uri uri)
        {
            Source = uri.AbsoluteUri;
            SourceChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public void GoBack() { }

        public void GoForward() { }

        public void Reload() { }

        public void RaiseTitle(string title)
        {
            Title = title;
            TitleChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseProcessFailure() => ProcessFailed?.Invoke(this, EventArgs.Empty);

        public void RaiseNavigationFailure() => NavigationFailed?.Invoke(this, EventArgs.Empty);

        public void RaiseNavigationCompleted(bool success, string status) =>
            NavigationCompleted?.Invoke(this, new TabNavigationResultEventArgs(success, status));

        public void RaisePopup(string? targetUrl) => PopupRequested?.Invoke(this, new PopupRequestedEventArgs(targetUrl));

        public void RaiseDownloadStarted(string sourceUrl, string fileName, string targetPath) =>
            DownloadStarted?.Invoke(this, new DownloadStartedEventArgs(sourceUrl, fileName, "application/octet-stream", targetPath));

        public void RaiseDownloadStatus(BrowserDownloadState state, string targetPath, string? interruptReason = null) =>
            DownloadStatusChanged?.Invoke(this, new DownloadStatusChangedEventArgs(state, targetPath, "https://example.com/file", 10, interruptReason));

        public void RaisePermission(BrowserPermissionKind kind, string sourceUrl, bool isAllowed) =>
            PermissionRequested?.Invoke(this, new PermissionRequestedEventArgs(kind, sourceUrl, isAllowed));

        public void Dispose() => IsDisposed = true;

        public void SetVisible(bool visible) => IsVisible = visible;

        public void SetBounds(int x, int y, int width, int height) => Bounds = (x, y, width, height);

        public void SetRasterizationScale(double scale) => RasterizationScale = scale;

        public void Focus() { }
    }
}
