using System;
using System.Collections.Generic;
using System.Linq;

namespace Brezza.Core.Tabs;

public sealed class TabRegistry
{
    private readonly List<BrowserTab> _tabs = new();
    private int _nextId;

    public event EventHandler? Changed;

    public IReadOnlyList<BrowserTab> Tabs => _tabs.ToArray();

    public BrowserTab? ActiveTab { get; private set; }

    public BrowserTab Create(string? initialUrl = null, BrowserSessionKind sessionKind = BrowserSessionKind.Normal)
    {
        string id;
        do
        {
            id = "tab-" + (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        while (_tabs.Any(tab => tab.Id == id));

        var tab = new BrowserTab(id, initialUrl, sessionKind);
        _tabs.Add(tab);
        Changed?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    public BrowserTab Restore(string id, string? url, string? title)
    {
        if (string.IsNullOrWhiteSpace(id) || _tabs.Any(tab => tab.Id == id))
        {
            throw new ArgumentException("Restored tab id must be non-empty and unique.", nameof(id));
        }

        var normalizedTitle = title?.Trim() ?? string.Empty;
        var normalizedUrl = string.Equals(url, "brezza://newtab", StringComparison.OrdinalIgnoreCase) ? string.Empty : url;
        var tab = new BrowserTab(id, normalizedUrl)
        {
            Title = normalizedTitle.Length == 0 ? "New Tab" : normalizedTitle,
        };
        _tabs.Add(tab);
        if (id.StartsWith("tab-", StringComparison.Ordinal) &&
            int.TryParse(id.Substring(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence))
        {
            _nextId = Math.Max(_nextId, sequence);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    public BrowserTab Activate(string tabId)
    {
        var tab = Find(tabId);
        ActiveTab = tab;
        Changed?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    public BrowserTab? Close(string tabId)
    {
        var index = _tabs.FindIndex(tab => tab.Id == tabId);
        if (index < 0) return ActiveTab;
        var wasActive = ReferenceEquals(_tabs[index], ActiveTab);
        _tabs.RemoveAt(index);
        if (wasActive) ActiveTab = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        Changed?.Invoke(this, EventArgs.Empty);
        return ActiveTab;
    }

    public bool Contains(string tabId) => _tabs.Any(tab => tab.Id == tabId);

    public bool HasSession(BrowserSessionKind sessionKind) => _tabs.Any(tab => tab.SessionKind == sessionKind);

    public void SetUrl(string tabId, string url) => Update(tabId, url: url);

    public BrowserTab Find(string tabId) =>
        _tabs.FirstOrDefault(tab => tab.Id == tabId) ?? throw new ArgumentException("Tab does not exist.", nameof(tabId));

    internal void Update(string tabId, string? url = null, string? title = null, TabRuntimeState? state = null)
    {
        if (!Contains(tabId)) return;
        var tab = Find(tabId);
        if (url is not null) tab.Url = url;
        if (title is not null) tab.Title = string.IsNullOrWhiteSpace(title) ? "New Tab" : title;
        if (state.HasValue) tab.State = state.Value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
