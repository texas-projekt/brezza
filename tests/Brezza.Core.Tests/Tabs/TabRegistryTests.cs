using Brezza.Core.Tabs;

namespace Brezza.Core.Tests.Tabs;

public sealed class TabRegistryTests
{
    [Fact]
    public void New_tabs_start_without_a_content_url()
    {
        var registry = new TabRegistry();

        var tab = registry.Create();

        Assert.Empty(tab.Url);
    }
}
