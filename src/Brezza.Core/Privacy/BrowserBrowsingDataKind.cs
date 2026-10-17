namespace Brezza.Core.Privacy;

/// <summary>
/// The categories of local browsing data the browser lets the user clear. Core owns the policy:
/// which runtime data kinds each category covers is the adapter's mapping, and <see cref="All"/>
/// is their sum rather than a whole-profile wipe, so a clear never removes more than the user was
/// shown.
/// </summary>
[Flags]
public enum BrowserBrowsingDataKind
{
    None = 0,

    /// <summary>Cached files: the disk cache and CacheStorage.</summary>
    Cache = 1,

    /// <summary>Data sites store in this browser: local storage, IndexedDB, Web SQL, file systems.</summary>
    SiteData = 2,

    /// <summary>Service workers sites registered.</summary>
    ServiceWorkers = 4,

    /// <summary>HTTP cookies; managed in detail by the cookies panel.</summary>
    Cookies = 8,

    All = Cache | SiteData | ServiceWorkers | Cookies,
}
