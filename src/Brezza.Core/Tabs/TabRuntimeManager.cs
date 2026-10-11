using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Brezza.Core.Abstractions;
using Brezza.Core.Navigation;

namespace Brezza.Core.Tabs;

public sealed class TabRuntimeManager
{
    private readonly TabRegistry _registry;
    private readonly ITabHostFactory _factory;
    private readonly Dictionary<string, ITabHost> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<ITabHost?>> _creating = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _restoring = new(StringComparer.Ordinal);
    private bool _disposed;

    public TabRuntimeManager(TabRegistry registry, ITabHostFactory factory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public ITabHost? ActiveHost { get; private set; }

    public event EventHandler? ActiveHostChanged;

    public event EventHandler<TabNavigationCompletedEventArgs>? NavigationCompleted;

    public async Task ActivateAsync(string tabId, bool waitForNavigation = true)
    {
        if (_disposed) return;
        var tab = _registry.Activate(tabId);

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
        if (!string.IsNullOrWhiteSpace(tab.Url) &&
            Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) &&
            !SourcesMatch(host.Source, uri))
        {
            var restoration = RestoreUrlAsync(tab, host, uri);
            if (waitForNavigation) await restoration.ConfigureAwait(true);
            else ObserveNavigation(tab, restoration);
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
            await _factory.CleanupSessionAsync(BrowserSessionKind.Private).ConfigureAwait(true);
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
        if (waitForNavigation) await navigation.ConfigureAwait(true);
        else ObserveNavigation(_registry.Find(tabId), navigation);
    }

    public void GoBack() => ActiveHost?.GoBack();

    public void GoForward() => ActiveHost?.GoForward();

    public void Reload() => ActiveHost?.Reload();

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
        ActiveHost = null;
        ActiveHostChanged?.Invoke(this, EventArgs.Empty);
        await _factory.CleanupSessionAsync(BrowserSessionKind.Private).ConfigureAwait(true);
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

    private void ObserveNavigation(BrowserTab tab, Task navigation)
    {
        _ = ObserveNavigationAsync(tab, navigation);
    }

    private async Task ObserveNavigationAsync(BrowserTab tab, Task navigation)
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
            _registry.Update(tab.Id, state: TabRuntimeState.Failed);
        }
    }

    private static bool SourcesMatch(string source, Uri uri)
    {
        var expected = NavigationInputParser.IsInternalPage(uri)
            ? NavigationInputParser.GetCanonicalInternalUrl(uri)
            : uri.AbsoluteUri;
        return string.Equals(source, expected, StringComparison.OrdinalIgnoreCase);
    }

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
            host.NavigationFailed += (_, _) => _registry.Update(tab.Id, state: TabRuntimeState.Failed);
            host.NavigationCompleted += (_, result) =>
            {
                if (result.IsSuccess)
                {
                    _registry.Update(tab.Id, url: host.Source, title: host.Title);
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
