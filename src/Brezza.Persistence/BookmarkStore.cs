using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Brezza.Persistence;

public sealed class BookmarkStore
{
    private readonly string _path;
    private readonly List<BookmarkRecord> _entries = new();

    public BookmarkStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        Load();
    }

    public IReadOnlyList<BookmarkRecord> GetAll(int limit = 500)
    {
        return _entries
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .Select(item => new BookmarkRecord { Url = item.Url, Title = item.Title })
            .ToArray();
    }

    public bool Contains(string url) => _entries.Any(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));

    public void Add(string url, string? title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        var canonicalUrl = uri.AbsoluteUri;
        if (Contains(canonicalUrl)) return;
        var normalizedTitle = title?.Trim() ?? string.Empty;
        _entries.Add(new BookmarkRecord
        {
            Url = canonicalUrl,
            Title = normalizedTitle.Length == 0 ? uri.Host : normalizedTitle,
        });
    }

    public bool Remove(string url)
    {
        var entry = _entries.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        return entry is not null && _entries.Remove(entry);
    }

    public void Save() => AtomicXmlFile.Save(_path, new BookmarkDocument { Entries = _entries.ToList() });

    private void Load()
    {
        var document = AtomicXmlFile.Load<BookmarkDocument>(_path);
        if (document is null || document.SchemaVersion != BookmarkDocument.CurrentSchemaVersion || document.Entries is null) return;
        foreach (var entry in document.Entries)
        {
            if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                Add(uri.AbsoluteUri, entry.Title);
            }
        }
    }

    [XmlRoot("bookmarks")]
    public sealed class BookmarkDocument
    {
        public const int CurrentSchemaVersion = 1;

        [XmlAttribute("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [XmlElement("entry")]
        public List<BookmarkRecord>? Entries { get; set; } = new();
    }

    public sealed class BookmarkRecord
    {
        [XmlElement("url")]
        public string Url { get; set; } = string.Empty;

        [XmlElement("title")]
        public string Title { get; set; } = string.Empty;
    }
}
