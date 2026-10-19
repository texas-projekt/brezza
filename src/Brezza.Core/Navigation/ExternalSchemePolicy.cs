using System;

namespace Brezza.Core.Navigation;

/// <summary>
/// Policy for URI schemes that leave the browser and launch another application. A page may request
/// them, but only a real user gesture may launch a small allowlist; script-initiated or unknown
/// schemes are cancelled.
/// </summary>
public static class ExternalSchemePolicy
{
    public static bool IsAllowed(string? uri, bool userInitiated)
    {
        if (!userInitiated || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        return string.Equals(parsed.Scheme, "mailto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parsed.Scheme, "tel", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parsed.Scheme, "sms", StringComparison.OrdinalIgnoreCase);
    }
}
