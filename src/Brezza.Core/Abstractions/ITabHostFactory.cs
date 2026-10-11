using System.Threading.Tasks;
using Brezza.Core.Tabs;

namespace Brezza.Core.Abstractions;

public interface ITabHostFactory
{
    Task<ITabHost> CreateAsync(string tabId, BrowserSessionKind sessionKind);

    Task CleanupSessionAsync(BrowserSessionKind sessionKind);
}
