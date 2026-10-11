using Brezza.Persistence;

namespace Brezza.Core.Tests.Persistence;

public sealed class BookmarkStoreTests
{
    [Fact]
    public void Adds_removes_and_persists_web_bookmarks()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "bookmarks.xml");
        var store = new BookmarkStore(path);
        store.Add("https://example.com/", "Example");
        store.Add("https://example.com/", "Duplicate");
        store.Add("file:///C:/secret.html", "Local file");
        store.Save();

        var restored = new BookmarkStore(path);
        var bookmark = Assert.Single(restored.GetAll());
        Assert.Equal("https://example.com/", bookmark.Url);
        Assert.True(restored.Remove(bookmark.Url));
        restored.Save();

        Assert.Empty(new BookmarkStore(path).GetAll());
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
