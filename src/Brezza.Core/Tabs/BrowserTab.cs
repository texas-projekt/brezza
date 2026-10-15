namespace Brezza.Core.Tabs;

public enum TabRuntimeState
{
    Dormant,
    Creating,
    Active,
    Sleeping,
    Failed,
    Closing,
}

public sealed class BrowserTab
{
    internal BrowserTab(string id, string? url, BrowserSessionKind sessionKind = BrowserSessionKind.Normal)
    {
        Id = id;
        Url = url ?? string.Empty;
        SessionKind = sessionKind;
        Title = "New Tab";
    }

    public string Id { get; }

    public BrowserSessionKind SessionKind { get; }

    public string Url { get; internal set; }

    public string Title { get; internal set; }

    /// <summary>
    /// The favicon image URI the current document reported, or empty when the document has none.
    /// Core tracks the URI only; the image is fetched through the tab's host, so it stays inside
    /// the page's own profile and network stack.
    /// </summary>
    public string FaviconUri { get; internal set; } = string.Empty;

    public TabRuntimeState State { get; internal set; }
}
