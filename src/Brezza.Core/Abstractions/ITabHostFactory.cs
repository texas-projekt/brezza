using System.Threading.Tasks;
using Brezza.Core.Tabs;

namespace Brezza.Core.Abstractions;

public interface ITabHostFactory
{
    Task<ITabHost> CreateAsync(string tabId, BrowserSessionKind sessionKind);

    /// <summary>
    /// Removes the local data of a browser session and reports what happened. A result that is not
    /// complete keeps the folder tracked so a later call can retry it.
    /// </summary>
    Task<SessionCleanupResult> CleanupSessionAsync(BrowserSessionKind sessionKind);
}
