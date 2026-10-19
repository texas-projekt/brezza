using System;
using Brezza.Core.Tabs;

namespace Brezza.Core.Abstractions;

/// <summary>A page asked the browser to open a new window or tab.</summary>
public sealed class PopupRequestedEventArgs : EventArgs
{
    public PopupRequestedEventArgs(string? targetUrl, BrowserSessionKind sessionKind = BrowserSessionKind.Normal,
        bool isUserInitiated = false, string tabId = "")
    {
        TargetUrl = targetUrl ?? string.Empty;
        SessionKind = sessionKind;
        IsUserInitiated = isUserInitiated;
        TabId = tabId ?? string.Empty;
    }

    /// <summary>The requested destination, or empty when the page opened a blank window.</summary>
    public string TargetUrl { get; }

    /// <summary>The originating tab's session, preserved even when another tab is active.</summary>
    public BrowserSessionKind SessionKind { get; }

    /// <summary>True when the request came from a user gesture rather than page script.</summary>
    public bool IsUserInitiated { get; }

    /// <summary>The originating tab, used by the popup rate limiter.</summary>
    public string TabId { get; }
}
