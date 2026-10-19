using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Privacy;
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

public sealed class WebView2TabHostFactory : ITabHostFactory, IBrowserDataManager
{
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private const string OwnerFileName = "owner.txt";
    private static readonly TimeSpan UnknownProfileGrace = TimeSpan.FromHours(24);

    // How long a single cookie deletion is given to show up in the store the runtime owns.
    private static readonly TimeSpan CookieDeleteSettleTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CookieDeletePollInterval = TimeSpan.FromMilliseconds(50);

    private readonly string _userDataRoot;
    private readonly IntPtr _parentWindow;
    private readonly DownloadRequestLimiter _downloadLimiter = new();
    private string? _downloadsFolder;
    private readonly Color _defaultBackgroundColor;
    private Task<CoreWebView2Environment>? _normalEnvironmentTask;
    private Task<CoreWebView2Environment>? _privateEnvironmentTask;
    private string? _privateUserDataFolder;

    /// <summary>Private profile folders that are not verified removed yet.</summary>
    private readonly List<string> _pendingPrivateFolders = new();

    // A hidden controller that exists only to reach the normal profile's cookie manager and
    // clear-browsing-data API. It is created on first use, is never visible, and never navigates,
    // so the shell can manage profile data while no tab holds a content host. It shares the normal
    // environment, so it costs no extra browser process, and it lives as long as the app like the
    // normal environment itself.
    private CoreWebView2Controller? _managementController;
    private readonly SemaphoreSlim _managementGate = new(1, 1);

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
        DiscoverOrphanedPrivateProfiles();
    }

    public string? PrivateUserDataFolder => _privateUserDataFolder;

    /// <summary>
    /// Folder downloads are written to. Setting it changes where the next download lands; the
    /// environments and hosts already created keep working, so no restart is needed. Empty falls
    /// back to the user's own Downloads folder.
    /// </summary>
    public string DownloadsFolder
    {
        get => string.IsNullOrWhiteSpace(_downloadsFolder) ? KnownFolders.Downloads() : _downloadsFolder!;
        set => _downloadsFolder = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public async Task<ITabHost> CreateAsync(string tabId, BrowserSessionKind sessionKind)
    {
        var environment = await GetEnvironmentAsync(sessionKind).ConfigureAwait(true);
        var options = CreateControllerOptions(environment, sessionKind);
        return WebView2TabHost.Create(environment, _parentWindow, options, () => DownloadsFolder, _downloadLimiter);
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

    // ---------- Profile data (IBrowserDataManager) ----------

    public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync()
    {
        try
        {
            var profile = await GetNormalProfileAsync().ConfigureAwait(true);
            var cookies = await profile.CookieManager.GetCookiesAsync(null).ConfigureAwait(true);
            return cookies
                .Select(MapCookie)
                .OrderBy(cookie => cookie.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (IsDataAccessFailure(exception))
        {
            System.Diagnostics.Trace.TraceWarning("The stored cookies could not be read: {0}", exception.Message);
            return Array.Empty<BrowserCookie>();
        }
    }

    public async Task<BrowsingDataClearResult> RemoveCookieAsync(BrowserCookie cookie)
    {
        try
        {
            var profile = await GetNormalProfileAsync().ConfigureAwait(true);
            // The cookie carries its own identity, so deleting it needs no second read of the store.
            profile.CookieManager.DeleteCookiesWithDomainAndPath(cookie.Name, cookie.Domain, cookie.Path);
            // A deletion is applied by the runtime after the call returns, so the store is read back
            // until it agrees: a row that is still listed is a cookie that is still stored.
            return await WaitUntilCookieIsGoneAsync(profile, cookie).ConfigureAwait(true)
                ? new BrowsingDataClearResult(BrowsingDataClearState.Completed, null)
                : new BrowsingDataClearResult(BrowsingDataClearState.Failed, "The cookie is still stored after it was deleted.");
        }
        catch (Exception exception) when (IsDataAccessFailure(exception))
        {
            System.Diagnostics.Trace.TraceWarning("The cookie could not be deleted: {0}", exception.Message);
            return new BrowsingDataClearResult(BrowsingDataClearState.Failed, exception.Message);
        }
    }

    /// <summary>
    /// Reads the cookie store back until the deleted cookie is gone, or the settle budget runs out.
    /// The budget keeps a deletion the runtime never applies from hanging the panel's refresh.
    /// </summary>
    private static async Task<bool> WaitUntilCookieIsGoneAsync(CoreWebView2Profile profile, BrowserCookie cookie)
    {
        var deadline = DateTime.UtcNow + CookieDeleteSettleTimeout;
        while (true)
        {
            var stored = await profile.CookieManager.GetCookiesAsync(null).ConfigureAwait(true);
            var stillThere = stored.Any(item =>
                string.Equals(item.Name, cookie.Name, StringComparison.Ordinal) &&
                string.Equals(item.Domain, cookie.Domain, StringComparison.Ordinal) &&
                string.Equals(item.Path, cookie.Path, StringComparison.Ordinal));
            if (!stillThere) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(CookieDeletePollInterval).ConfigureAwait(true);
        }
    }

    public async Task<BrowsingDataClearResult> ClearAsync(BrowserBrowsingDataKind kinds)
    {
        if (kinds == BrowserBrowsingDataKind.None)
        {
            return new BrowsingDataClearResult(BrowsingDataClearState.Completed, null);
        }

        try
        {
            var profile = await GetNormalProfileAsync().ConfigureAwait(true);
            await profile.ClearBrowsingDataAsync(MapKinds(kinds)).ConfigureAwait(true);
            return new BrowsingDataClearResult(BrowsingDataClearState.Completed, null);
        }
        catch (Exception exception) when (IsDataAccessFailure(exception))
        {
            System.Diagnostics.Trace.TraceWarning("The browsing data could not be cleared: {0}", exception.Message);
            return new BrowsingDataClearResult(BrowsingDataClearState.Failed, exception.Message);
        }
    }

    /// <summary>
    /// Maps the categories the user is shown onto the runtime data kinds each one covers. The
    /// mapping belongs to the adapter: Core decides which categories exist, not which runtime
    /// kinds they name, and <see cref="BrowserBrowsingDataKind.All"/> stays their sum so a clear
    /// never removes more than the panel showed.
    /// </summary>
    private static CoreWebView2BrowsingDataKinds MapKinds(BrowserBrowsingDataKind kinds)
    {
        var mapped = default(CoreWebView2BrowsingDataKinds);
        if (kinds.HasFlag(BrowserBrowsingDataKind.Cache))
        {
            mapped |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;
        }

        if (kinds.HasFlag(BrowserBrowsingDataKind.SiteData))
        {
            mapped |= CoreWebView2BrowsingDataKinds.LocalStorage |
                CoreWebView2BrowsingDataKinds.IndexedDb |
                CoreWebView2BrowsingDataKinds.WebSql |
                CoreWebView2BrowsingDataKinds.FileSystems;
        }

        if (kinds.HasFlag(BrowserBrowsingDataKind.ServiceWorkers))
        {
            mapped |= CoreWebView2BrowsingDataKinds.ServiceWorkers;
        }

        if (kinds.HasFlag(BrowserBrowsingDataKind.Cookies))
        {
            mapped |= CoreWebView2BrowsingDataKinds.Cookies;
        }

        return mapped;
    }

    private static BrowserCookie MapCookie(CoreWebView2Cookie cookie)
    {
        // A session cookie has no expiry; the runtime reports the default date for it.
        DateTime? expires = cookie.Expires == default ? (DateTime?)null : cookie.Expires;
        return new BrowserCookie(
            cookie.Name,
            cookie.Value,
            cookie.Domain,
            cookie.Path,
            expires,
            cookie.IsSecure,
            cookie.IsHttpOnly,
            cookie.SameSite switch
            {
                CoreWebView2CookieSameSiteKind.Lax => BrowserCookieSameSite.Lax,
                CoreWebView2CookieSameSiteKind.Strict => BrowserCookieSameSite.Strict,
                _ => BrowserCookieSameSite.None,
            });
    }

    private async Task<CoreWebView2Profile> GetNormalProfileAsync()
    {
        var controller = await GetManagementControllerAsync().ConfigureAwait(true);
        return controller.CoreWebView2.Profile;
    }

    /// <summary>
    /// The hidden management controller, created once and reused. A failed creation is not cached,
    /// so a later clear attempt starts a fresh one.
    /// </summary>
    private async Task<CoreWebView2Controller> GetManagementControllerAsync()
    {
        if (_managementController is not null) return _managementController;
        await _managementGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_managementController is not null) return _managementController;
            var environment = await GetEnvironmentAsync(BrowserSessionKind.Normal).ConfigureAwait(true);
            var options = CreateControllerOptions(environment, BrowserSessionKind.Normal);
            var controller = await environment
                .CreateCoreWebView2ControllerAsync(_parentWindow, options)
                .ConfigureAwait(true);
            // The bridge stays disabled so this surface can never become a content surface, and it
            // is never shown: it exists for the profile's data APIs only.
            controller.CoreWebView2.Settings.IsWebMessageEnabled = false;
            controller.IsVisible = false;
            _managementController = controller;
            return controller;
        }
        finally
        {
            _managementGate.Release();
        }
    }

    private static bool IsDataAccessFailure(Exception exception) =>
        exception is InvalidOperationException ||
            exception is System.Runtime.InteropServices.COMException ||
            exception is IOException ||
            exception is UnauthorizedAccessException;

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

    /// <summary>
    /// Finds private profiles a previous process left behind. A profile whose owner is still running
    /// is skipped, so a second Brezza instance is never deleted; a profile without an owner marker
    /// is only treated as orphaned after a day, because older builds did not write one.
    /// </summary>
    private void DiscoverOrphanedPrivateProfiles()
    {
        var container = Path.Combine(_userDataRoot, "PrivateSessions");
        if (!Directory.Exists(container)) return;

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(container))
            {
                if (IsOwnedByLiveProcess(folder)) continue;

                var marker = Path.Combine(folder, OwnerFileName);
                if (!File.Exists(marker) &&
                    Directory.GetCreationTimeUtc(folder) > DateTime.UtcNow - UnknownProfileGrace)
                {
                    continue;
                }

                _pendingPrivateFolders.Add(folder);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Orphaned private profiles could not be inspected: {0}", exception.Message);
        }
    }

    private static bool IsOwnedByLiveProcess(string folder)
    {
        var marker = Path.Combine(folder, OwnerFileName);
        if (!File.Exists(marker)) return false;

        try
        {
            if (!int.TryParse(File.ReadAllText(marker).Trim(), out var processId) || processId <= 0) return false;
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException ||
            exception is ArgumentException || exception is InvalidOperationException)
        {
            return false;
        }
    }

    private static void WriteOwnerMarker(string folder)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(folder, OwnerFileName), current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // The marker is an optimization for startup cleanup, not a requirement for isolation.
            System.Diagnostics.Trace.TraceWarning(
                "A private profile owner marker could not be written: {0}", exception.Message);
        }
    }

    private async Task<CoreWebView2Environment> GetEnvironmentAsync(BrowserSessionKind sessionKind)
    {
        if (sessionKind == BrowserSessionKind.Normal)
        {
            if (_normalEnvironmentTask is null)
            {
                Directory.CreateDirectory(_userDataRoot);
                _normalEnvironmentTask = CoreWebView2Environment.CreateAsync(null, _userDataRoot);
            }

            try
            {
                return await _normalEnvironmentTask.ConfigureAwait(true);
            }
            catch
            {
                // A transient runtime failure must not poison every later tab in this process.
                _normalEnvironmentTask = null;
                throw;
            }
        }

        if (_privateEnvironmentTask is null)
        {
            // Reuse the same profile folder across retries: creating another one here would leak
            // the first folder, which no tab ever references.
            _privateUserDataFolder ??= Path.Combine(_userDataRoot, "PrivateSessions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_privateUserDataFolder);
            WriteOwnerMarker(_privateUserDataFolder);
            _privateEnvironmentTask = CoreWebView2Environment.CreateAsync(null, _privateUserDataFolder);
        }

        try
        {
            return await _privateEnvironmentTask.ConfigureAwait(true);
        }
        catch
        {
            _privateEnvironmentTask = null;
            throw;
        }
    }
}
