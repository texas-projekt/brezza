using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Tabs;

namespace Brezza.Persistence;

/// <summary>
/// The durable list of downloads. Only downloads that reached a terminal state in a normal
/// session are written: an in-progress download is not a finished record, and a private download
/// is never persisted. The store owns the file format; <see cref="DownloadRegistry"/> owns the
/// live list the shell renders.
/// </summary>
public sealed class DownloadStore
{
    private const int MaximumEntries = 500;

    private readonly string _path;
    private readonly List<DownloadRecord> _entries = new();

    public DownloadStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        Load();
    }

    /// <summary>The stored downloads, newest first.</summary>
    public IReadOnlyList<BrowserDownload> GetAll(int limit = 500)
    {
        return _entries
            .OrderByDescending(item => item.StartedUtcTicks)
            .Take(Math.Max(0, limit))
            .Select(ToDownload)
            .ToArray();
    }

    /// <summary>
    /// Writes the downloads that belong in the durable list: terminal records of normal sessions,
    /// newest first. In-progress and private downloads are left out.
    /// </summary>
    public void Save(IEnumerable<BrowserDownload> downloads)
    {
        if (downloads is null) throw new ArgumentNullException(nameof(downloads));
        _entries.Clear();
        _entries.AddRange(downloads
            .Where(item => item.IsTerminal && !item.IsPrivate)
            .OrderByDescending(item => item.StartedUtc)
            .Take(MaximumEntries)
            .Select(ToRecord));
        AtomicXmlFile.Save(_path, new DownloadDocument { Entries = _entries.ToList() });
    }

    private void Load()
    {
        var document = AtomicXmlFile.Load<DownloadDocument>(_path);
        if (document is null || document.SchemaVersion != DownloadDocument.CurrentSchemaVersion || document.Entries is null) return;

        foreach (var entry in document.Entries.Take(MaximumEntries))
        {
            if (IsValid(entry)) _entries.Add(entry);
        }
    }

    /// <summary>
    /// A stored record is usable when it names a file and holds a finished outcome. Only terminal
    /// records are ever written, so anything else in the file was put there by hand.
    /// </summary>
    private static bool IsValid(DownloadRecord entry) =>
        !string.IsNullOrWhiteSpace(entry.Id) &&
        !string.IsNullOrWhiteSpace(entry.TargetPath) &&
        entry.State is (int)BrowserDownloadState.Completed or (int)BrowserDownloadState.Interrupted;

    private static DownloadRecord ToRecord(BrowserDownload download) => new()
    {
        Id = download.Id,
        Url = download.SourceUrl,
        FileName = download.FileName,
        TargetPath = download.TargetFilePath,
        State = (int)download.State,
        StartedUtcTicks = download.StartedUtc.ToUniversalTime().Ticks,
        CompletedUtcTicks = download.CompletedUtc?.ToUniversalTime().Ticks,
        TotalBytes = download.TotalBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        ReceivedBytes = download.ReceivedBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        InterruptReason = download.InterruptReason ?? string.Empty,
    };

    private static BrowserDownload ToDownload(DownloadRecord record) => new(
        record.Id,
        record.Url,
        string.IsNullOrWhiteSpace(record.FileName) ? Path.GetFileName(record.TargetPath) : record.FileName,
        record.TargetPath,
        (BrowserDownloadState)record.State,
        new DateTime(record.StartedUtcTicks, DateTimeKind.Utc),
        // A private download is never written, so every stored record is a normal one.
        BrowserSessionKind.Normal,
        record.CompletedUtcTicks is { } completed ? new DateTime(completed, DateTimeKind.Utc) : (DateTime?)null,
        ulong.TryParse(record.TotalBytes, NumberStyles.None, CultureInfo.InvariantCulture, out var total) ? total : (ulong?)null,
        record.InterruptReason.Length == 0 ? null : record.InterruptReason,
        ulong.TryParse(record.ReceivedBytes, NumberStyles.None, CultureInfo.InvariantCulture, out var received) ? received : (ulong?)null);

    [XmlRoot("downloads")]
    public sealed class DownloadDocument
    {
        public const int CurrentSchemaVersion = 1;

        [XmlAttribute("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [XmlElement("entry")]
        public List<DownloadRecord>? Entries { get; set; } = new();
    }

    public sealed class DownloadRecord
    {
        [XmlElement("id")]
        public string Id { get; set; } = string.Empty;

        [XmlElement("url")]
        public string Url { get; set; } = string.Empty;

        [XmlElement("fileName")]
        public string FileName { get; set; } = string.Empty;

        [XmlElement("targetPath")]
        public string TargetPath { get; set; } = string.Empty;

        [XmlElement("state")]
        public int State { get; set; }

        [XmlElement("startedUtcTicks")]
        public long StartedUtcTicks { get; set; }

        [XmlElement("completedUtcTicks")]
        public long? CompletedUtcTicks { get; set; }

        [XmlElement("totalBytes")]
        public string TotalBytes { get; set; } = string.Empty;

        [XmlElement("receivedBytes")]
        public string ReceivedBytes { get; set; } = string.Empty;

        [XmlElement("interruptReason")]
        public string InterruptReason { get; set; } = string.Empty;
    }
}
