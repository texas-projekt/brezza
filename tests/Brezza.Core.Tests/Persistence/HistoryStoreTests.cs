using Brezza.Persistence;

namespace Brezza.Core.Tests.Persistence;

public sealed class HistoryStoreTests
{
    [Fact]
    public void Persists_successful_web_visits_and_updates_repeat_visits()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "history.xml");
        var store = new HistoryStore(path);
        store.Record("https://example.com/first", "Example");
        store.Record("https://example.com/first", "Example Home");
        store.Record("file:///C:/private.html", "Private file");
        store.Save();

        var restored = new HistoryStore(path).GetAll();

        var entry = Assert.Single(restored);
        Assert.Equal(2, entry.VisitCount);
        Assert.Equal("Example Home", entry.Title);
    }

    [Fact]
    public void Corrupt_history_falls_back_to_empty()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "history.xml");
        File.WriteAllText(path, "<history><broken>");

        Assert.Empty(new HistoryStore(path).GetAll());
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
