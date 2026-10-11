namespace Brezza.Core.Tabs;

public enum TabRuntimeState
{
    Dormant,
    Creating,
    Active,
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

    public TabRuntimeState State { get; internal set; }
}
