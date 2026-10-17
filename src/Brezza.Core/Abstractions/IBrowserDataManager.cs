using System.Collections.Generic;
using System.Threading.Tasks;
using Brezza.Core.Privacy;

namespace Brezza.Core.Abstractions;

/// <summary>
/// Reads and clears the local browsing data of the browser's normal profile: its cookies and the
/// data its sites store. Private-session data is not managed here; it is removed when the last
/// private tab closes, the same way private tabs are excluded from history and saved sessions.
/// </summary>
public interface IBrowserDataManager
{
    /// <summary>Every cookie the profile stores, ordered for display.</summary>
    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync();

    /// <summary>Deletes one cookie, identified by its name, domain, and path.</summary>
    Task<BrowsingDataClearResult> RemoveCookieAsync(BrowserCookie cookie);

    /// <summary>Clears the given categories of browsing data.</summary>
    Task<BrowsingDataClearResult> ClearAsync(BrowserBrowsingDataKind kinds);
}
