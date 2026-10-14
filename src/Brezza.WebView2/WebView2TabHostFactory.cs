using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Brezza.Core.Abstractions;
using Brezza.Core.Tabs;
using Microsoft.Web.WebView2.Core;

namespace Brezza.WebView2;

/// <summary>Converts between Win32 COLORREF values and the colors WebView2 takes.</summary>
public static class WebView2Color
{
    /// <summary>Converts a Win32 COLORREF to the color a WebView2 surface is created with.</summary>
    public static System.Drawing.Color FromColorref(uint colorref) =>
        System.Drawing.Color.FromArgb(
            unchecked((byte)(colorref & 0xFF)),
            unchecked((byte)((colorref >> 8) & 0xFF)),
            unchecked((byte)((colorref >> 16) & 0xFF)));
}

public sealed class WebView2TabHostFactory : ITabHostFactory
{
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _userDataRoot;
    private readonly IntPtr _parentWindow;
    private readonly string? _downloadsFolder;
    private readonly Color _defaultBackgroundColor;
    private Task<CoreWebView2Environment>? _normalEnvironmentTask;
    private Task<CoreWebView2Environment>? _privateEnvironmentTask;
    private string? _privateUserDataFolder;

    /// <summary>Private profile folders that are not verified removed yet.</summary>
    private readonly List<string> _pendingPrivateFolders = new();

    /// <param name="parentWindow">Native window the WebView2 controllers are parented to.</param>
    /// <param name="defaultBackgroundColor">
    /// Surface color a controller is created with. The color a controller shows before a page
    /// paints is fixed at creation time; creating one with white and correcting it afterwards is
    /// what flashes a white block while a page is loading.
    /// </param>
    public WebView2TabHostFactory(string userDataRoot, IntPtr parentWindow, string? downloadsFolder = null,
        Color? defaultBackgroundColor = null)
    {
        _userDataRoot = userDataRoot;
        _parentWindow = parentWindow;
        _downloadsFolder = downloadsFolder;
        _defaultBackgroundColor = defaultBackgroundColor ?? Color.White;
    }

    public string? PrivateUserDataFolder => _privateUserDataFolder;

    /// <summary>Folder downloads are written to; defaults to the user's Downloads folder.</summary>
    public string DownloadsFolder =>
        string.IsNullOrWhiteSpace(_downloadsFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : _downloadsFolder!;

    public async Task<ITabHost> CreateAsync(string tabId, BrowserSessionKind sessionKind)
    {
        var environment = await GetEnvironmentAsync(sessionKind).ConfigureAwait(true);
        var options = CreateControllerOptions(environment, sessionKind);
        return WebView2TabHost.Create(environment, _parentWindow, options, DownloadsFolder);
    }

    public async Task<SessionCleanupResult> CleanupSessionAsync(BrowserSessionKind sessionKind)
    {
        if (sessionKind != BrowserSessionKind.Private) return NothingToRemove(sessionKind);

        var targets = new List<string>();
        if (_privateUserDataFolder is not null)
        {
            targets.Add(_privateUserDataFolder);
            _privateUserDataFolder = null;
            _privateEnvironmentTask = null;
        }

        targets.AddRange(_pendingPrivateFolders);
        _pendingPrivateFolders.Clear();
        if (targets.Count == 0) return NothingToRemove(sessionKind);

        var deadline = DateTime.UtcNow + CleanupBudget;
        var attempts = 0;
        string? lastDetail = null;
        var retryable = true;
        foreach (var folder in targets)
        {
            while (Directory.Exists(folder) && DateTime.UtcNow < deadline)
            {
                attempts++;
                try
                {
                    Directory.Delete(folder, recursive: true);
                    lastDetail = null;
                    retryable = true;
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    // The WebView2 process can still hold the profile right after its controllers close.
                    lastDetail = exception.Message;
                    retryable = true;
                    await Task.Delay(RetryDelay).ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    lastDetail = exception.Message;
                    retryable = false;
                    break;
                }
            }

            if (Directory.Exists(folder))
            {
                // Keep the path tracked until removal is verified, so a later attempt can retry it.
                _pendingPrivateFolders.Add(folder);
            }
        }

        RemoveEmptyParent();
        var primary = targets[0];
        if (_pendingPrivateFolders.Count == 0)
        {
            return new SessionCleanupResult(sessionKind, SessionCleanupState.Completed, primary, attempts, lastDetail);
        }

        var state = retryable ? SessionCleanupState.Pending : SessionCleanupState.Failed;
        return new SessionCleanupResult(sessionKind, state, primary, attempts, lastDetail);
    }

    private static SessionCleanupResult NothingToRemove(BrowserSessionKind sessionKind) =>
        new(sessionKind, SessionCleanupState.Completed, string.Empty, 0, null);

    private CoreWebView2ControllerOptions CreateControllerOptions(CoreWebView2Environment environment, BrowserSessionKind sessionKind)
    {
        // The wrapper requires options for every controller; private mode is the only difference.
        var options = environment.CreateCoreWebView2ControllerOptions();
        if (sessionKind == BrowserSessionKind.Private)
        {
            options.IsInPrivateModeEnabled = true;
        }

        options.DefaultBackgroundColor = _defaultBackgroundColor;
        return options;
    }

    private void RemoveEmptyParent()
    {
        var container = Path.Combine(_userDataRoot, "PrivateSessions");
        if (!Directory.Exists(container) || Directory.EnumerateFileSystemEntries(container).Any()) return;
        try
        {
            Directory.Delete(container);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // The container is only a grouping directory; leaving it behind is not a data leak.
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
