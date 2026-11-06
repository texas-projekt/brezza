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

    [Fact]
    public void A_tab_created_after_another_is_placed_right_after_it()
    {
        var registry = new TabRegistry();
        var first = registry.Create("https://example.com/1");
        var second = registry.Create("https://example.com/2");
        var third = registry.Create("https://example.com/3");

        var copy = registry.Create(first.Url, afterTabId: first.Id);
        var appended = registry.Create(afterTabId: "tab-unknown");

        Assert.Equal(new[] { first.Id, copy.Id, second.Id, third.Id, appended.Id }, registry.Tabs.Select(tab => tab.Id));
    }

    [Fact]
    public void Closing_the_active_tab_activates_its_right_neighbour_or_the_new_last_tab()
    {
        var registry = new TabRegistry();
        var first = registry.Create();
        var second = registry.Create();
        var third = registry.Create();

        registry.Activate(second.Id);
        Assert.Equal(third.Id, registry.Close(second.Id)?.Id);

        Assert.Equal(first.Id, registry.Close(third.Id)?.Id);
        Assert.Null(registry.Close(first.Id));
        Assert.Null(registry.ActiveTab);
    }
}
