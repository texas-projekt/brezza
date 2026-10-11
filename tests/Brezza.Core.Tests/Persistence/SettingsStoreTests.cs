using Brezza.Persistence;

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
