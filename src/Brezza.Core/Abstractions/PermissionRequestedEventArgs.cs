using System;

namespace Brezza.Core.Abstractions;

/// <summary>A page asked for a capability, and Core answered the request.</summary>
public sealed class PermissionRequestedEventArgs : EventArgs
{
    public PermissionRequestedEventArgs(Brezza.Core.Permissions.BrowserPermissionKind kind, string sourceUrl, bool isAllowed)
    {
        Kind = kind;
        SourceUrl = sourceUrl ?? string.Empty;
        IsAllowed = isAllowed;
    }

    public Brezza.Core.Permissions.BrowserPermissionKind Kind { get; }

    public string SourceUrl { get; }

    public bool IsAllowed { get; }
}
