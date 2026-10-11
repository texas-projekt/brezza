using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Brezza.Core.Abstractions;
using Brezza.Core.Tabs;
using Microsoft.Web.WebView2.Core;

namespace Brezza.WebView2;

public sealed class WebView2TabHostFactory : ITabHostFactory
{
    private readonly string _userDataRoot;
    private readonly string? _trustedAssetsDirectory;
    private Task<CoreWebView2Environment>? _normalEnvironmentTask;
    private Task<CoreWebView2Environment>? _privateEnvironmentTask;
    private string? _privateUserDataFolder;

    public WebView2TabHostFactory(string userDataRoot, string? trustedAssetsDirectory = null)
    {
        _userDataRoot = userDataRoot;
        _trustedAssetsDirectory = trustedAssetsDirectory;
    }

    public string? PrivateUserDataFolder => _privateUserDataFolder;

    public async Task<ITabHost> CreateAsync(string tabId, BrowserSessionKind sessionKind)
    {
        var environment = await GetEnvironmentAsync(sessionKind);
        return await WebView2TabHost.CreateAsync(environment, _trustedAssetsDirectory, sessionKind == BrowserSessionKind.Private);
    }

    public async Task CleanupSessionAsync(BrowserSessionKind sessionKind)
    {
        if (sessionKind != BrowserSessionKind.Private || _privateUserDataFolder is null) return;
        var folder = _privateUserDataFolder;
        _privateUserDataFolder = null;
        _privateEnvironmentTask = null;

        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (Directory.Exists(folder) && DateTime.UtcNow < deadline)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                await Task.Delay(250).ConfigureAwait(true);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(250).ConfigureAwait(true);
            }
        }

        var parent = Path.GetDirectoryName(folder);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }
    }

    private Task<CoreWebView2Environment> GetEnvironmentAsync(BrowserSessionKind sessionKind)
    {
        if (sessionKind == BrowserSessionKind.Normal)
        {
            if (_normalEnvironmentTask is not null) return _normalEnvironmentTask;
            Directory.CreateDirectory(_userDataRoot);
            _normalEnvironmentTask = CoreWebView2Environment.CreateAsync(null, _userDataRoot);
            return _normalEnvironmentTask;
        }

        if (_privateEnvironmentTask is not null) return _privateEnvironmentTask;
        _privateUserDataFolder = Path.Combine(_userDataRoot, "PrivateSessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_privateUserDataFolder);
        _privateEnvironmentTask = CoreWebView2Environment.CreateAsync(null, _privateUserDataFolder);
        return _privateEnvironmentTask;
    }
}
