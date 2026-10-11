using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Brezza.Core.Bridge;
using Microsoft.Web.WebView2.Core;

namespace Brezza.WebView2;

public partial class WebView2ChromeHost : UserControl, IDisposable
{
    private readonly TaskCompletionSource<bool> _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Environment? _environment;
    private string? _assetsDirectory;
    private bool _initializationStarted;

    public WebView2ChromeHost()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public bool IsReady => _initialized.Task.Status == TaskStatus.RanToCompletion;

    public event EventHandler<ChromeMessageEventArgs>? MessageReceived;

    public event EventHandler<string>? PageNavigated;

    public async Task InitializeAsync(string assetsDirectory, string userDataFolder)
    {
        _assetsDirectory = Path.GetFullPath(assetsDirectory);
        Directory.CreateDirectory(userDataFolder);
        _environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        if (ChromeWebView.IsLoaded) StartInitialization();
        await _initialized.Task;
    }

    public void PostMessage(string json)
    {
        if (!IsReady) return;
        ChromeWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    public Task<string> ExecuteScriptAsync(string script)
    {
        if (!IsReady) throw new InvalidOperationException("Chrome WebView2 is not initialized.");
        return ChromeWebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    public async Task NavigateAsync(string relativePage)
    {
        if (!IsReady) throw new InvalidOperationException("Chrome WebView2 is not initialized.");
        var page = relativePage.TrimStart('/');
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, _) => completion.TrySetResult(true);
        ChromeWebView.CoreWebView2.NavigationCompleted += handler;
        try
        {
            ChromeWebView.CoreWebView2.Navigate("https://brezza.local/" + page);
            await completion.Task.ConfigureAwait(true);
        }
        finally
        {
            ChromeWebView.CoreWebView2.NavigationCompleted -= handler;
        }
    }

    public void Dispose()
    {
        Loaded -= OnLoaded;
        _initialized.TrySetCanceled();
        ChromeWebView.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_environment is not null) StartInitialization();
    }

    private async void StartInitialization()
    {
        if (_initializationStarted || _environment is null || _assetsDirectory is null) return;
        _initializationStarted = true;

        try
        {
            await ChromeWebView.EnsureCoreWebView2Async(_environment);
            var core = ChromeWebView.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(
                "brezza.local",
                _assetsDirectory,
                CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            core.NavigationStarting += (_, args) =>
            {
                if (!TrustedChromeOriginPolicy.IsTrusted(args.Uri)) args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.WebMessageReceived += (_, args) =>
            {
                if (!TrustedChromeOriginPolicy.IsTrusted(args.Source)) return;
                try
                {
                    if (args.TryGetWebMessageAsString() is string json)
                    {
                        MessageReceived?.Invoke(this, new ChromeMessageEventArgs(json));
                    }
                }
                catch (ArgumentException)
                {
                    // Non-string messages are rejected at the trust boundary.
                }
            };
            core.NavigationCompleted += (_, _) => PageNavigated?.Invoke(this, core.Source);

            core.Navigate("https://brezza.local/index.html");
            _initialized.TrySetResult(true);
        }
        catch (Exception exception)
        {
            _initialized.TrySetException(exception);
        }
    }

}
