using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Brezza.Core.Navigation;
using Brezza.Core.Tabs;

namespace Brezza.Persistence;

public sealed class SessionStore
{
    private readonly string _path;

    public SessionStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public BrowserSession Load()
    {
        var session = AtomicXmlFile.Load<BrowserSession>(_path);
        if (session is null || session.SchemaVersion != BrowserSession.CurrentSchemaVersion || session.Tabs is null)
        {
            return new BrowserSession();
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        session.Tabs = session.Tabs
            .Where(tab => !string.IsNullOrWhiteSpace(tab.Id) && ids.Add(tab.Id) && IsRestorableUrl(tab.Url))
            .Select(tab =>
            {
                if (string.Equals(tab.Url, "brezza://newtab", StringComparison.OrdinalIgnoreCase)) tab.Url = string.Empty;
                return tab;
            })
            .ToList();
        if (session.Tabs.All(tab => !string.Equals(tab.Id, session.ActiveTabId, StringComparison.Ordinal)))
        {
            session.ActiveTabId = session.Tabs.FirstOrDefault()?.Id;
        }

        return session;
    }

    public void Save(IEnumerable<BrowserTab> tabs, string? activeTabId)
    {
        var entries = tabs
            .Where(tab => tab.SessionKind == BrowserSessionKind.Normal)
            .Select(tab => new SessionTabRecord
        {
            Id = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
        }).ToList();
        var activeId = entries.Any(tab => tab.Id == activeTabId) ? activeTabId : entries.FirstOrDefault()?.Id;
        AtomicXmlFile.Save(_path, new BrowserSession { ActiveTabId = activeId, Tabs = entries });
    }

    private static bool IsRestorableUrl(string url) =>
        string.IsNullOrWhiteSpace(url) ||
        (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile ||
          NavigationInputParser.IsInternalPage(uri) ||
          (string.Equals(uri.Scheme, "brezza", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(uri.Host, "newtab", StringComparison.OrdinalIgnoreCase))));

    [XmlRoot("session")]
    public sealed class BrowserSession
    {
        public const int CurrentSchemaVersion = 1;

        [XmlAttribute("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [XmlAttribute("activeTabId")]
        public string? ActiveTabId { get; set; }

        [XmlElement("tab")]
        public List<SessionTabRecord>? Tabs { get; set; } = new();
    }

    public sealed class SessionTabRecord
    {
        [XmlAttribute("id")]
        public string Id { get; set; } = string.Empty;

        [XmlElement("url")]
        public string Url { get; set; } = string.Empty;

        [XmlElement("title")]
        public string Title { get; set; } = string.Empty;
    }
}
