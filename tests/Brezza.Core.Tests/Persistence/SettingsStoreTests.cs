using Brezza.Persistence;
using Brezza.Core.Navigation;

namespace Brezza.Core.Tests.Persistence;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Saves_and_loads_search_template_atomically()
    {
        using var directory = new TestDirectory();
        var store = new SettingsStore(Path.Combine(directory.Path, "settings.xml"));
        store.Save(new BrowserSettings { SearchTemplate = "https://search.example/find?q={query}" });

        Assert.Equal("https://search.example/find?q={query}", store.Load().SearchTemplate);
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings.xml.tmp")));
    }

    [Fact]
    public void Persists_supported_language_and_falls_back_for_unknown_language()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.xml");
        var store = new SettingsStore(path);
        store.Save(new BrowserSettings { Language = "zh-Hans" });
        Assert.Equal("zh-Hans", store.Load().Language);

        store.Save(new BrowserSettings { Language = "fr" });
        Assert.Equal(string.Empty, store.Load().Language);
    }

    [Fact]
    public void Corrupt_or_incompatible_settings_fall_back_to_defaults()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.xml");
        File.WriteAllText(path, "<settings schemaVersion=\"99\"><searchTemplate>bad</searchTemplate></settings>");

        var settings = new SettingsStore(path).Load();

        Assert.Equal(BrowserSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("https://www.bing.com/search?q={query}", settings.SearchTemplate);
    }

    [Fact]
    public void Persists_theme_and_upgrades_legacy_schema()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.xml");
        var store = new SettingsStore(path);
        store.Save(new BrowserSettings { Theme = BrowserThemeMode.Dark });

        var loaded = store.Load();
        Assert.Equal(BrowserThemeMode.Dark, loaded.Theme);
        Assert.Equal(BrowserSettings.CurrentSchemaVersion, loaded.SchemaVersion);

        File.WriteAllText(path, "<settings schemaVersion=\"1\"><searchTemplate>https://www.bing.com/search?q={query}</searchTemplate></settings>");
        Assert.Equal(BrowserThemeMode.System, store.Load().Theme);
    }

    [Fact]
    public void Startup_behavior_defaults_to_the_new_tab_page_and_persists_the_choice()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.xml");
        var store = new SettingsStore(path);

        // A profile without a settings file and a schema 2 file without the value both start from
        // the new-tab page; the rest of the older file still applies.
        Assert.Equal(BrowserStartupBehavior.NewTab, store.Load().StartupBehavior);
        File.WriteAllText(path,
            "<settings schemaVersion=\"2\"><searchTemplate>https://search.example/find?q={query}</searchTemplate><theme>Dark</theme></settings>");
        var legacy = store.Load();
        Assert.Equal(BrowserStartupBehavior.NewTab, legacy.StartupBehavior);
        Assert.Equal(BrowserThemeMode.Dark, legacy.Theme);
        Assert.Equal("https://search.example/find?q={query}", legacy.SearchTemplate);

        store.Save(new BrowserSettings { StartupBehavior = BrowserStartupBehavior.RestoreSession });
        Assert.Equal(BrowserStartupBehavior.RestoreSession, store.Load().StartupBehavior);
    }

    [Fact]
    public void Unknown_startup_behavior_falls_back_to_the_new_tab_page()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.xml");
        File.WriteAllText(path, "<settings schemaVersion=\"3\"><startupBehavior>7</startupBehavior></settings>");

        Assert.Equal(BrowserStartupBehavior.NewTab, new SettingsStore(path).Load().StartupBehavior);
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
