using Brezza.Core.Tabs;
using Brezza.Core.Navigation;
using Brezza.Persistence;

namespace Brezza.Core.Tests.Persistence;

public sealed class SessionStoreTests
{
    [Fact]
    public void Restores_duplicate_tabs_and_the_active_tab_identity()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(Path.Combine(directory.Path, "session.xml"));
        var tabs = new TabRegistry();
        var first = tabs.Create("https://example.com/");
        var second = tabs.Create("https://example.com/");
        tabs.Activate(second.Id);

        store.Save(tabs.Tabs, tabs.ActiveTab?.Id);
        var restored = store.Load();
        var registry = new TabRegistry();
        foreach (var tab in restored.Tabs!) registry.Restore(tab.Id, tab.Url, tab.Title);
        registry.Activate(restored.ActiveTabId!);

        Assert.Equal(first.Id, registry.Tabs[0].Id);
        Assert.Equal(second.Id, registry.ActiveTab!.Id);
        Assert.Equal(2, registry.Tabs.Count);
    }

    [Fact]
    public void Does_not_persist_private_tabs_or_the_private_active_tab()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(Path.Combine(directory.Path, "session.xml"));
        var tabs = new TabRegistry();
        var normal = tabs.Create("https://example.com/");
        var privateTab = tabs.Create("https://private.example/", BrowserSessionKind.Private);
        tabs.Activate(privateTab.Id);

        store.Save(tabs.Tabs, tabs.ActiveTab?.Id);
        var restored = store.Load();

        var saved = Assert.Single(restored.Tabs!);
        Assert.Equal(normal.Id, saved.Id);
        Assert.Equal(normal.Id, restored.ActiveTabId);
    }

    [Fact]
    public void Ignores_unsupported_and_non_web_tab_urls()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "session.xml");
        File.WriteAllText(path,
            "<session schemaVersion=\"1\" activeTabId=\"t1\"><tab id=\"t1\"><url>javascript:alert(1)</url></tab><tab id=\"t2\"><url>https://example.com/</url></tab></session>");

        var restored = new SessionStore(path).Load();

        Assert.Single(restored.Tabs!);
        Assert.Equal("t2", restored.ActiveTabId);
    }

    [Fact]
    public void Restores_local_file_tabs_but_rejects_script_schemes()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "session.xml");
        File.WriteAllText(path,
            "<session schemaVersion=\"1\" activeTabId=\"file\"><tab id=\"file\"><url>file:///C:/Users/test/page.html</url></tab><tab id=\"script\"><url>javascript:alert(1)</url></tab></session>");

        var restored = new SessionStore(path).Load();

        var tab = Assert.Single(restored.Tabs!);
        Assert.Equal("file", tab.Id);
        Assert.StartsWith("file:", tab.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restores_brezza_internal_pages()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "session.xml");
        File.WriteAllText(path,
            "<session schemaVersion=\"1\" activeTabId=\"settings\"><tab id=\"settings\"><url>brezza://settings</url></tab></session>");

        var restored = new SessionStore(path).Load();

        var tab = Assert.Single(restored.Tabs!);
        Assert.Equal("brezza://settings", tab.Url);
    }

    [Fact]
    public void Legacy_empty_tab_is_migrated_to_the_internal_new_tab_url()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "session.xml");
        File.WriteAllText(path,
            "<session schemaVersion=\"1\" activeTabId=\"new\"><tab id=\"new\"><url></url></tab></session>");

        var restored = new SessionStore(path).Load();
        Assert.Equal(NavigationInputParser.NewTabUrl, restored.Tabs![0].Url);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(AppContext.BaseDirectory, ".test-data", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
