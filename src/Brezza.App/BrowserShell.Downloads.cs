using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Brezza.App.Native;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Navigation;
using Brezza.Core.Permissions;
using Brezza.Core.Privacy;
using Brezza.Core.Tabs;
using Brezza.Persistence;
using Brezza.WebView2;

namespace Brezza.App;

/// <summary>The download list: row actions, the durable store, and how a download row reads.</summary>
internal sealed partial class BrowserShell
{
    // ---------- Downloads ----------

    /// <summary>
    /// Removes one entry from the download list. The record is the user's own, so it goes without
    /// a confirmation, and the downloaded file itself is never touched.
    /// </summary>
    private void RemoveDownload(string downloadId)
    {
        if (_downloads?.Remove(downloadId) == true) SaveDownloads();
        RefreshDownloadsPanel();
    }

    /// <summary>Clears the whole download list. Only the records go; the files stay where they are.</summary>
    private void ClearDownloads()
    {
        _downloads?.Clear();
        // The store is rewritten from the now-empty list, so the file holds no entries either.
        SaveDownloads();
        RefreshDownloadsPanel();
    }

    private void OpenDownloadFile(string targetFilePath) => Observe(() =>
    {
        // The browser never renders a downloaded file itself; the shell opens it like Explorer does.
        OpenWithShell(targetFilePath);
        return Task.CompletedTask;
    });

    private void OpenDownloadFolder(string targetFilePath) => Observe(() =>
    {
        OpenWithShell("explorer.exe", "/select,\"" + targetFilePath + "\"");
        return Task.CompletedTask;
    });

    private static void OpenWithShell(string target, string? arguments = null)
    {
        var info = new ProcessStartInfo(target) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(arguments)) info.Arguments = arguments;
        Process.Start(info);
    }

    /// <summary>
    /// Writes the durable download list. The store keeps terminal records of normal sessions only,
    /// so a download that is still running, or one from a private tab, never reaches the disk.
    /// </summary>
    private void SaveDownloads()
    {
        if (_downloadStore is null || _downloads is null) return;
        try
        {
            _downloadStore.Save(_downloads.Downloads);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            TraceWarning("The download list could not be saved", exception);
        }
    }

    /// <summary>Refreshes the downloads panel only while it is the panel the user has open.</summary>
    private void RefreshDownloadsPanel()
    {
        if (string.Equals(_panelMode, "downloads", StringComparison.Ordinal)) RefreshPanel();
    }

    /// <summary>
    /// The folder downloads are written to right now: the saved one, or the factory's default of the
    /// user's own Downloads folder. The settings form shows it, so the field is never empty.
    /// </summary>
    private string EffectiveDownloadsFolder => _settings?.DownloadsFolder is { Length: > 0 } saved
        ? saved
        : _hostFactory?.DownloadsFolder ?? string.Empty;

    private List<PanelEntry> DownloadEntries() =>
        _downloads?.Downloads
            .Select(download => new PanelEntry(DownloadTitle(download), DownloadDetail(download), download: download))
            .ToList() ?? new List<PanelEntry>();

    private static string DownloadTitle(BrowserDownload download) =>
        string.IsNullOrWhiteSpace(download.FileName) ? Path.GetFileName(download.TargetFilePath) : download.FileName;

    /// <summary>The second line of a download row: where it came from, how it ended, and when.</summary>
    private string DownloadDetail(BrowserDownload download)
    {
        var parts = new List<string>();
        // An absolute address can still have no host (a blob or a data URL), and an empty part
        // would only add a stray separator to the row.
        if (Uri.TryCreate(download.SourceUrl, UriKind.Absolute, out var source))
        {
            if (source.Host.Length > 0) parts.Add(source.Host);
        }
        else if (download.SourceUrl.Length > 0)
        {
            parts.Add(download.SourceUrl);
        }

        parts.Add(_localization.Get(DownloadStateKey(download.State)));

        if (download.IsRunning)
        {
            // A running download shows what has arrived against the total, and the share when the
            // runtime knows one; without a total there is no share, only the bytes.
            if (download.TotalBytes is { } total && total > 0)
            {
                var received = Math.Min(download.ReceivedBytes ?? 0, total);
                parts.Add(FormatByteSize(received) + " / " + FormatByteSize(total));
                parts.Add(Percent(received, total));
            }
            else if (download.ReceivedBytes is { } arrived)
            {
                parts.Add(FormatByteSize(arrived));
            }
        }
        else
        {
            // The runtime does not always know the size, so it is shown only when it reported one.
            if (download.TotalBytes is { } bytes) parts.Add(FormatByteSize(bytes));
            if (download.State == BrowserDownloadState.Interrupted && !string.IsNullOrWhiteSpace(download.InterruptReason))
            {
                parts.Add(LocalizedInterruptReason(download.InterruptReason!));
            }

            parts.Add((download.CompletedUtc ?? download.StartedUtc).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        return string.Join(" • ", parts);
    }

    /// <summary>The whole share of <paramref name="received"/> in <paramref name="total"/>.</summary>
    private static string Percent(ulong received, ulong total) =>
        (received >= total ? 100 : (int)Math.Round((double)received / total * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The runtime reports an interrupt reason as a technical name. The ones a user can act on are
    /// named in their language; anything else is shown as the runtime reported it rather than
    /// dropped, so a failure is never left unexplained.
    /// </summary>
    private string LocalizedInterruptReason(string reason)
    {
        var key = InterruptReasonKey(reason);
        if (key is null) return reason;
        var text = _localization.Get(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? reason : text;
    }

    /// <summary>
    /// The category a runtime interrupt reason belongs to. The runtime names dozens of reasons; a
    /// user needs to know whether to retry, free space, or sign in, not which enum it was.
    /// </summary>
    private static string? InterruptReasonKey(string reason) => reason switch
    {
        "UserCanceled" => "downloads.reason.cancelled",
        "UserShutdown" => "downloads.reason.browserClosed",
        "DownloadProcessCrashed" => "downloads.reason.browserProblem",
        "NetworkFailed" or "NetworkTimeout" or "NetworkDisconnected" or "NetworkServerDown" or "NetworkInvalidRequest"
            => "downloads.reason.network",
        "ServerUnauthorized" or "ServerForbidden" or "ServerFailed" or "ServerNoRange" or "ServerBadContent"
            or "ServerUnexpectedResponse" or "ServerCertificateProblem" or "ServerContentLengthMismatch"
            or "ServerCrossOriginRedirect" => "downloads.reason.server",
        "FileFailed" or "FileAccessDenied" or "FileNoSpace" or "FileNameTooLong" or "FileTooLarge"
            or "FileMalicious" or "FileTransientError" or "FileBlockedByPolicy" or "FileSecurityCheckFailed"
            or "FileTooShort" or "FileHashMismatch" => "downloads.reason.file",
        _ => null,
    };

    private static string DownloadStateKey(BrowserDownloadState state) => state switch
    {
        BrowserDownloadState.Completed => "downloads.state.completed",
        BrowserDownloadState.Interrupted => "downloads.state.interrupted",
        BrowserDownloadState.Paused => "downloads.state.paused",
        _ => "downloads.state.inProgress",
    };

    private static string FormatByteSize(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var value = unit == 0
            ? ((long)size).ToString(CultureInfo.InvariantCulture)
            : size.ToString("0.#", CultureInfo.InvariantCulture);
        return value + " " + units[unit];
    }

    private List<PanelEntry> HistoryEntries() =>
        _historyStore?.GetAll()
            .Select(item => new PanelEntry(item.Title, item.Url))
            .ToList() ?? new List<PanelEntry>();

    private List<PanelEntry> BookmarkEntries() =>
        _bookmarkStore?.GetAll()
            .Select(item => new PanelEntry(item.Title, item.Url))
            .ToList() ?? new List<PanelEntry>();

    private List<PanelEntry> CookieEntries(IReadOnlyList<BrowserCookie> cookies) =>
        cookies
            .Select(cookie => new PanelEntry(cookie.Name, CookieDetail(cookie), cookie))
            .ToList();

    /// <summary>The four browsing-data categories, in the order the panel lists them.</summary>
    private List<PanelEntry> DataCategoryEntries() => new()
    {
        DataCategory(BrowserBrowsingDataKind.Cache),
        DataCategory(BrowserBrowsingDataKind.SiteData),
        DataCategory(BrowserBrowsingDataKind.ServiceWorkers),
        DataCategory(BrowserBrowsingDataKind.Cookies),
    };

    private PanelEntry DataCategory(BrowserBrowsingDataKind kind) => new(
        _localization.Get(CategoryTitleKey(kind)),
        _localization.Get(CategoryDescriptionKey(kind)),
        dataKind: kind);

    private static string CategoryTitleKey(BrowserBrowsingDataKind kind) => kind switch
    {
        BrowserBrowsingDataKind.Cache => "data.category.cache",
        BrowserBrowsingDataKind.SiteData => "data.category.siteData",
        BrowserBrowsingDataKind.ServiceWorkers => "data.category.serviceWorkers",
        _ => "data.category.cookies",
    };

    private static string CategoryDescriptionKey(BrowserBrowsingDataKind kind) => kind switch
    {
        BrowserBrowsingDataKind.Cache => "data.category.cacheDescription",
        BrowserBrowsingDataKind.SiteData => "data.category.siteDataDescription",
        BrowserBrowsingDataKind.ServiceWorkers => "data.category.serviceWorkersDescription",
        _ => "data.category.cookiesDescription",
    };

    /// <summary>The second line of a cookie row: where it lives, when it ends, and how it is protected.</summary>
    private string CookieDetail(BrowserCookie cookie)
    {
        var parts = new List<string> { cookie.Domain };
        // The default path says nothing about a cookie, so it is shown only when a site chose one.
        if (!string.Equals(cookie.Path, "/", StringComparison.Ordinal)) parts.Add(cookie.Path);
        parts.Add(cookie.ExpiresUtc is { } expires
            ? _localization.Format("cookies.expires", "date", expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : _localization.Get("cookies.session"));
        if (cookie.IsSecure) parts.Add(_localization.Get("cookies.secure"));
        if (cookie.IsHttpOnly) parts.Add(_localization.Get("cookies.httpOnly"));
        parts.Add(_localization.Format("cookies.sameSite", "value", cookie.SameSite.ToString()));
        return string.Join(" • ", parts);
    }

    private IReadOnlyList<string> GetAddressSuggestions()
    {
        var suggestions = new List<string>();
        if (_historyStore is not null)
        {
            suggestions.AddRange(_historyStore.GetAll(20).Select(item => item.Url));
        }

        if (_tabs is not null)
        {
            suggestions.AddRange(_tabs.Tabs
                .Where(tab => !string.IsNullOrWhiteSpace(tab.Url))
                .Select(tab => tab.Url));
        }

        return suggestions
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }
}
