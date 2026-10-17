namespace Brezza.Core.Privacy;

/// <summary>How a cookie is scoped to the site that set it.</summary>
public enum BrowserCookieSameSite
{
    None,
    Lax,
    Strict,
}

/// <summary>
/// One cookie a profile stores: its identity, its value, and the flags the site set. A cookie
/// without an expiry is a session cookie that the profile drops when the browser closes.
/// </summary>
public sealed class BrowserCookie
{
    public BrowserCookie(
        string name,
        string value,
        string domain,
        string path,
        DateTime? expiresUtc,
        bool isSecure,
        bool isHttpOnly,
        BrowserCookieSameSite sameSite)
    {
        Name = name ?? string.Empty;
        Value = value ?? string.Empty;
        Domain = domain ?? string.Empty;
        Path = path ?? string.Empty;
        ExpiresUtc = expiresUtc;
        IsSecure = isSecure;
        IsHttpOnly = isHttpOnly;
        SameSite = sameSite;
    }

    public string Name { get; }

    public string Value { get; }

    public string Domain { get; }

    public string Path { get; }

    public DateTime? ExpiresUtc { get; }

    public bool IsSecure { get; }

    public bool IsHttpOnly { get; }

    public BrowserCookieSameSite SameSite { get; }

    /// <summary>True for a session cookie, which has no expiry and is dropped when the browser closes.</summary>
    public bool IsSession => ExpiresUtc is null;
}
