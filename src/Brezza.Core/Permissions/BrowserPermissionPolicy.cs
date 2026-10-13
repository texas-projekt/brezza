namespace Brezza.Core.Permissions;

/// <summary>Capability groups the shell answers permission requests for.</summary>
public enum BrowserPermissionKind
{
    Unknown,
    MediaCapture,
    Geolocation,
    Notifications,
    Clipboard,
    FileSystem,
    Other,
}

/// <summary>Core's answer to a permission request.</summary>
public sealed class BrowserPermissionDecision
{
    private BrowserPermissionDecision(bool isAllowed, BrowserPermissionKind kind, string reason)
    {
        IsAllowed = isAllowed;
        Kind = kind;
        Reason = reason;
    }

    public bool IsAllowed { get; }

    public BrowserPermissionKind Kind { get; }

    /// <summary>Why the request was answered this way; kept for diagnostics and tests.</summary>
    public string Reason { get; }

    public static BrowserPermissionDecision Grant(BrowserPermissionKind kind) => new(true, kind, "granted by policy");

    public static BrowserPermissionDecision Deny(BrowserPermissionKind kind) =>
        new(false, kind, "the shell has no permission prompt, so requests are never granted silently");
}

/// <summary>
/// Owns the browser's permission decisions. The shell deliberately grants nothing on its own: a
/// request is answered only after the user is told, so no page can take a capability silently.
/// </summary>
public static class BrowserPermissionPolicy
{
    public static BrowserPermissionDecision Evaluate(BrowserPermissionKind kind) =>
        BrowserPermissionDecision.Deny(kind);
}
