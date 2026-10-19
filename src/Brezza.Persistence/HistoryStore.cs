using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Brezza.Persistence;

public sealed class HistoryStore
{
    private const int MaximumEntries = 5000;
    private readonly string _path;
    private readonly List<HistoryRecord> _entries = new();

    public HistoryStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        Load();
    }

    public IReadOnlyList<HistoryRecord> GetAll(int limit = 500)
    {
        return _entries
            .OrderByDescending(item => item.LastVisitedUtc)
            .Take(Math.Max(0, limit))
            .Select(item => new HistoryRecord
            {
                Url = item.Url,
                Title = item.Title,
                LastVisitedUtc = item.LastVisitedUtc,
                VisitCount = item.VisitCount,
            })
            .ToArray();
    }

    public void Record(string url, string? title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;

        var canonicalUrl = uri.AbsoluteUri;
        var entry = _entries.FirstOrDefault(item => string.Equals(item.Url, canonicalUrl, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new HistoryRecord { Url = canonicalUrl };
            _entries.Add(entry);
        }

        var normalizedTitle = title?.Trim() ?? string.Empty;
        entry.Title = normalizedTitle.Length == 0 ? uri.Host : normalizedTitle;
        entry.LastVisitedUtc = DateTime.UtcNow;
        entry.VisitCount++;
        if (_entries.Count > MaximumEntries)
        {
            _entries.Remove(_entries.OrderBy(item => item.LastVisitedUtc).First());
        }
    }

    public bool Remove(string url)
    {
        var entry = _entries.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        return entry is not null && _entries.Remove(entry);
    }

    public void Clear() => _entries.Clear();

    public void Save()
    {
        AtomicXmlFile.Save(
            _path,
            new HistoryDocument { Entries = _entries.OrderByDescending(item => item.LastVisitedUtc).ToList() });
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            using var stream = File.OpenRead(_path);
            if (new XmlSerializer(typeof(HistoryDocument)).Deserialize(stream) is not HistoryDocument document ||
                document.SchemaVersion != HistoryDocument.CurrentSchemaVersion || document.Entries is null)
            {
                return;
            }

            foreach (var entry in document.Entries.Take(MaximumEntries))
            {
                if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                    entry.VisitCount > 0)
                {
                    entry.Url = uri.AbsoluteUri;
                    entry.Title = string.IsNullOrWhiteSpace(entry.Title) ? uri.Host : entry.Title;
                    _entries.Add(entry);
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidOperationException || exception is ArgumentException)
        {
            _entries.Clear();
            if (exception is not IOException) AtomicXmlFile.Quarantine(_path, exception);
        }
    }

    [XmlRoot("history")]
    public sealed class HistoryDocument
    {
        public const int CurrentSchemaVersion = 1;

        [XmlAttribute("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [XmlElement("entry")]
        public List<HistoryRecord>? Entries { get; set; } = new();
    }

    public sealed class HistoryRecord
    {
        [XmlElement("url")]
        public string Url { get; set; } = string.Empty;

        [XmlElement("title")]
        public string Title { get; set; } = string.Empty;

        [XmlElement("lastVisitedUtc")]
        public DateTime LastVisitedUtc { get; set; }

        [XmlElement("visitCount")]
        public int VisitCount { get; set; }
    }
}
