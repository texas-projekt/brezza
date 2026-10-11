using Brezza.Core.Abstractions;
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
    public async Task Closing_during_creation_closes_the_new_host_and_removes_it()
    {
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
        var activating = manager.ActivateAsync(tab.Id);
        factory.Complete();
        await activating;

        await manager.CloseAsync(tab.Id);

        Assert.Equal(BrowserSessionKind.Private, factory.LastSessionKind);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, factory.CleanupCount);
    }

    private sealed class DelayedFactory : ITabHostFactory
    {
        private readonly TaskCompletionSource<ITabHost> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCount { get; private set; }

        public BrowserSessionKind LastSessionKind { get; private set; }

        public int CleanupCount { get; private set; }

        public Task<ITabHost> CreateAsync(string tabId, BrowserSessionKind sessionKind)
        {
            CreateCount++;
            LastSessionKind = sessionKind;
            return _completion.Task;
        }

        public Task CleanupSessionAsync(BrowserSessionKind sessionKind)
        {
            LastSessionKind = sessionKind;
            CleanupCount++;
            return Task.CompletedTask;
        }

        public FakeHost Complete()
        {
            var host = new FakeHost();
            _completion.SetResult(host);
            return host;
        }
    }

    private sealed class FakeHost : ITabHost
    {
        public string Source { get; private set; } = string.Empty;

        public string Title { get; private set; } = "New Tab";

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public bool IsDisposed { get; private set; }

        public event EventHandler? SourceChanged;

        public event EventHandler? TitleChanged;

        public event EventHandler? ProcessFailed;

        public event EventHandler? NavigationFailed;

        public event EventHandler<TabNavigationResultEventArgs>? NavigationCompleted;

        public event EventHandler<BrowserShortcutEventArgs>? ShortcutPressed;

        public event EventHandler<ChromeMessageEventArgs>? InternalMessageReceived;

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

        public void RaiseShortcut(int key, bool control = false, bool alt = false)
        {
            var args = new BrowserShortcutEventArgs(key, control, alt, shift: false);
            ShortcutPressed?.Invoke(this, args);
        }

        public void Dispose() => IsDisposed = true;

        public void PostMessage(string json) { }

        public void RaiseInternalMessage(string json) => InternalMessageReceived?.Invoke(this, new ChromeMessageEventArgs(json));
    }
}
