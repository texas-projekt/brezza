using Brezza.Core.Abstractions;

namespace Brezza.Core.Tabs;

/// <summary>
/// Observable signals of a live tab host. A missing or unknown signal must be reported as the
/// conservative value, so a host that cannot be evaluated is never released.
/// </summary>
public sealed class TabHostSignals
{
    public TabHostSignals(bool isLoading, bool isDocumentPlayingAudio, bool containsFullScreenElement)
    {
        IsLoading = isLoading;
        IsDocumentPlayingAudio = isDocumentPlayingAudio;
        ContainsFullScreenElement = containsFullScreenElement;
    }

    public static TabHostSignals FromHost(ITabHost host) =>
        host is null
            ? new TabHostSignals(isLoading: true, isDocumentPlayingAudio: false, containsFullScreenElement: false)
            : new TabHostSignals(host.IsLoading, host.IsDocumentPlayingAudio, host.ContainsFullScreenElement);

    /// <summary>True when a navigation is still in flight for the host.</summary>
    public bool IsLoading { get; }

    /// <summary>True when the document is playing audio.</summary>
    public bool IsDocumentPlayingAudio { get; }

    /// <summary>True when the document shows a full-screen element.</summary>
    public bool ContainsFullScreenElement { get; }
}

/// <summary>Why a tab host was or was not released. <see cref="Released"/> means the host is gone.</summary>
public enum TabSleepOutcome
{
    Released,
    UnknownTab,
    NoHost,
    ActiveTab,
    Loading,
    PlayingAudio,
    FullScreen,
}

/// <summary>
/// Decides whether an inactive tab host may be released. The rule is deliberately conservative:
/// anything that is still working stays alive.
/// </summary>
public static class TabSleepPolicy
{
    public static TabSleepOutcome Evaluate(TabHostSignals signals, bool isActiveTab)
    {
        if (isActiveTab) return TabSleepOutcome.ActiveTab;
        if (signals.IsLoading) return TabSleepOutcome.Loading;
        if (signals.IsDocumentPlayingAudio) return TabSleepOutcome.PlayingAudio;
        if (signals.ContainsFullScreenElement) return TabSleepOutcome.FullScreen;
        return TabSleepOutcome.Released;
    }
}
