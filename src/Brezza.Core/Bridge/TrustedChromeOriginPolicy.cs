using System;

namespace Brezza.Core.Bridge;

public static class TrustedChromeOriginPolicy
{
    public const string HostName = "brezza.local";

    public static bool IsTrusted(string? source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, HostName, StringComparison.OrdinalIgnoreCase) &&
               uri.IsDefaultPort &&
               string.IsNullOrEmpty(uri.UserInfo);
    }
}
