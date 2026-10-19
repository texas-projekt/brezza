using System;

namespace Brezza.Core.Abstractions;

/// <summary>
/// A navigation ended in failure. The id identifies the navigation the runtime reported, so a
/// failure that arrives after a newer navigation completed can be ignored instead of marking the
/// current page as failed.
/// </summary>
public sealed class TabNavigationFailedEventArgs : EventArgs
{
    public TabNavigationFailedEventArgs(ulong navigationId, string webErrorStatus)
    {
        NavigationId = navigationId;
        WebErrorStatus = webErrorStatus ?? string.Empty;
    }

    /// <summary>The runtime's navigation id, or 0 when the source cannot provide one.</summary>
    public ulong NavigationId { get; }

    public string WebErrorStatus { get; }
}
