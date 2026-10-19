using System;

namespace Brezza.Core.Abstractions;

/// <summary>A page requested an external URI scheme that the browser refused to launch.</summary>
public sealed class ExternalSchemeBlockedEventArgs : EventArgs
{
    public ExternalSchemeBlockedEventArgs(string uri, string initiatingOrigin)
    {
        Uri = uri ?? string.Empty;
        InitiatingOrigin = initiatingOrigin ?? string.Empty;
    }

    public string Uri { get; }

    public string InitiatingOrigin { get; }
}
