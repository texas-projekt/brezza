using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Brezza.Core.Abstractions;
using Brezza.Core.Bridge;
using Brezza.Core.Navigation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace Brezza.WebView2;

public sealed class WebView2TabHost : ITabHost
{
    private readonly CoreWebView2Environment _environment;
    private readonly WpfWebView2 _webView;
    private readonly TaskCompletionSource<bool> _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private bool _initializationStarted;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private TaskCompletionSource<bool>? _pendingNavigation;
    private readonly string? _trustedAssetsDirectory;
    private string? _logicalSource;

    internal WebView2TabHost(CoreWebView2Environment environment, string? trustedAssetsDirectory, bool isPrivate = false)
    {
        _environment = environment;
        _trustedAssetsDirectory = trustedAssetsDirectory;
        _webView = new WpfWebView2();
        if (isPrivate)
        {
            _webView.CreationProperties = new CoreWebView2CreationProperties
            {
                IsInPrivateModeEnabled = true,
            };
        }
        _webView.Loaded += OnWebViewLoaded;
        _webView.PreviewKeyDown += OnPreviewKeyDown;
    }

    public FrameworkElement View => _webView;

    public string Source => _logicalSource ?? _webView.Source?.AbsoluteUri ?? string.Empty;

    public string Title => _webView.CoreWebView2?.DocumentTitle ?? string.Empty;

    public bool CanGoBack => _webView.CoreWebView2?.CanGoBack ?? false;

    public bool CanGoForward => _webView.CoreWebView2?.CanGoForward ?? false;

    public event EventHandler? SourceChanged;

    public event EventHandler? TitleChanged;

    public event EventHandler? ProcessFailed;

    public event EventHandler? NavigationFailed;

    public event EventHandler<TabNavigationResultEventArgs>? NavigationCompleted;

    public event EventHandler<BrowserShortcutEventArgs>? ShortcutPressed;

    public event EventHandler<Brezza.Core.Abstractions.ChromeMessageEventArgs>? InternalMessageReceived;

    public static Task<WebView2TabHost> CreateAsync(string userDataFolder)
    {
        Directory.CreateDirectory(userDataFolder);
        return CreateAsync(CoreWebView2Environment.CreateAsync(null, userDataFolder));
    }

    internal static async Task<WebView2TabHost> CreateAsync(Task<CoreWebView2Environment> environmentTask, string? trustedAssetsDirectory = null)
    {
        return new WebView2TabHost(await environmentTask, trustedAssetsDirectory);
    }

    internal static Task<WebView2TabHost> CreateAsync(CoreWebView2Environment environment, string? trustedAssetsDirectory, bool isPrivate = false) =>
        Task.FromResult(new WebView2TabHost(environment, trustedAssetsDirectory, isPrivate));

    private async void OnWebViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_initializationStarted || _disposed) return;
        _initializationStarted = true;

        try
        {
            await _webView.EnsureCoreWebView2Async(_environment);
            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsWebMessageEnabled = _trustedAssetsDirectory is not null;
            if (_trustedAssetsDirectory is not null)
            {
                core.SetVirtualHostNameToFolderMapping(
                    TrustedChromeOriginPolicy.HostName,
                    _trustedAssetsDirectory,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
            core.SourceChanged += (_, _) =>
            {
                _logicalSource = GetLogicalSource(core.Source);
                SourceChanged?.Invoke(this, EventArgs.Empty);
            };
            core.DocumentTitleChanged += (_, _) => TitleChanged?.Invoke(this, EventArgs.Empty);
            core.ProcessFailed += (_, _) =>
            {
                _pendingNavigation?.TrySetException(new InvalidOperationException("The WebView2 process failed during navigation."));
                ProcessFailed?.Invoke(this, EventArgs.Empty);
            };
            core.NavigationCompleted += (_, args) =>
            {
                NavigationCompleted?.Invoke(this, new TabNavigationResultEventArgs(args.IsSuccess, args.WebErrorStatus.ToString()));
                if (!args.IsSuccess) NavigationFailed?.Invoke(this, EventArgs.Empty);
            };
            core.WebMessageReceived += (_, args) =>
            {
                if (_trustedAssetsDirectory is null || !TrustedChromeOriginPolicy.IsTrusted(args.Source)) return;
                try
                {
                    if (args.TryGetWebMessageAsString() is string json)
                    {
                        InternalMessageReceived?.Invoke(this, new Core.Abstractions.ChromeMessageEventArgs(json));
                    }
                }
                catch (ArgumentException) { }
            };
            _initialized.TrySetResult(true);
        }
        catch (Exception exception)
        {
            _initialized.TrySetException(exception);
        }
    }

    public async Task NavigateAsync(Uri uri)
    {
        ThrowIfDisposed();
        await _initialized.Task;
        await _navigationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            _logicalSource = NavigationInputParser.IsInternalPage(uri)
                ? NavigationInputParser.GetCanonicalInternalUrl(uri)
                : null;
            var actualUri = uri.Scheme.Equals("brezza", StringComparison.OrdinalIgnoreCase)
                ? new Uri("https://" + TrustedChromeOriginPolicy.HostName + "/" + uri.Host + ".html")
                : uri;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
            handler = (_, args) => completion.TrySetResult(args.IsSuccess);
            _pendingNavigation = completion;
            _webView.CoreWebView2.NavigationCompleted += handler;
            try
            {
                _webView.CoreWebView2.Navigate(actualUri.AbsoluteUri);
                var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(true);
                if (!ReferenceEquals(completed, completion.Task))
                {
                    throw new TimeoutException("WebView2 navigation did not complete within 30 seconds.");
                }

                await completion.Task.ConfigureAwait(true);
            }
            finally
            {
                _webView.CoreWebView2.NavigationCompleted -= handler;
                if (ReferenceEquals(_pendingNavigation, completion)) _pendingNavigation = null;
            }
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    public void GoBack()
    {
        ThrowIfDisposed();
        if (_webView.CanGoBack) _webView.GoBack();
    }

    public void GoForward()
    {
        ThrowIfDisposed();
        if (_webView.CanGoForward) _webView.GoForward();
    }

    public void Reload()
    {
        ThrowIfDisposed();
        _webView.Reload();
    }

    public void PostMessage(string json)
    {
        ThrowIfDisposed();
        if (_webView.CoreWebView2 is { } core) core.PostWebMessageAsJson(json);
    }

    public Task<string> ExecuteScriptAsync(string script)
    {
        ThrowIfDisposed();
        if (_webView.CoreWebView2 is null) throw new InvalidOperationException("WebView2 is not initialized.");
        return _webView.CoreWebView2.ExecuteScriptAsync(script);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pendingNavigation?.TrySetException(new ObjectDisposedException(nameof(WebView2TabHost)));
        _webView.Loaded -= OnWebViewLoaded;
        _webView.PreviewKeyDown -= OnPreviewKeyDown;
        _initialized.TrySetCanceled();
        _webView.Dispose();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var args = new BrowserShortcutEventArgs(
            KeyInterop.VirtualKeyFromKey(key),
            modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Alt),
            modifiers.HasFlag(ModifierKeys.Shift));
        ShortcutPressed?.Invoke(this, args);
        if (args.Handled) e.Handled = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WebView2TabHost));
    }

    private static string? GetLogicalSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            !TrustedChromeOriginPolicy.IsTrusted(uri.AbsoluteUri)) return null;

        var page = uri.AbsolutePath.Trim('/');
        if (page.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            page = page.Substring(0, page.Length - ".html".Length);
        }

        return page.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
            page.Equals("history", StringComparison.OrdinalIgnoreCase) ||
            page.Equals("bookmarks", StringComparison.OrdinalIgnoreCase) ||
            page.Equals("newtab", StringComparison.OrdinalIgnoreCase)
            ? "brezza://" + page.ToLowerInvariant()
            : null;
    }
}
