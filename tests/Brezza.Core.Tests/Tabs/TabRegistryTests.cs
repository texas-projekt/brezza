using Brezza.Core.Navigation;
using Brezza.Core.Tabs;

namespace Brezza.Core.Tests.Tabs;

public sealed class TabRegistryTests
{
    [Fact]
    public void New_tabs_start_on_the_canonical_internal_new_tab_page()
    {
        var registry = new TabRegistry();

        var tab = registry.Create();

        Assert.Equal(NavigationInputParser.NewTabUrl, tab.Url);
    }
}
