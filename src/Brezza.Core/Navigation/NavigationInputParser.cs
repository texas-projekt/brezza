using System;

namespace Brezza.Core.Navigation;

public sealed class NavigationInputParser
{
    public const string QueryPlaceholder = "{query}";
    public const string NewTabUrl = "brezza://newtab";

    private readonly string _searchTemplate;

    public NavigationInputParser(string? searchTemplate)
    {
        _searchTemplate = ValidateTemplate(searchTemplate);
    }

    public NavigationTarget Parse(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return NavigationTarget.Empty();
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && IsNavigableScheme(absolute.Scheme))
        {
            if (string.Equals(absolute.Scheme, "brezza", StringComparison.OrdinalIgnoreCase) &&
                !IsInternalPage(absolute))
            {
                return NavigationTarget.Search(text, new Uri(_searchTemplate.Replace(QueryPlaceholder, Uri.EscapeDataString(text)), UriKind.Absolute));
            }
            return NavigationTarget.Url(text, absolute);
        }

        if (LooksLikeHost(text) && Uri.TryCreate("https://" + text, UriKind.Absolute, out var host))
        {
            return NavigationTarget.Url(text, host);
        }

        var escaped = Uri.EscapeDataString(text);
        var searchUri = new Uri(_searchTemplate.Replace(QueryPlaceholder, escaped), UriKind.Absolute);
        return NavigationTarget.Search(text, searchUri);
    }

    public static string ValidateTemplate(string? template)
    {
        var value = template;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Search template must contain exactly one {query} placeholder.", nameof(template));
        }

        var queryIndex = value!.IndexOf(QueryPlaceholder, StringComparison.Ordinal);
        if (queryIndex < 0 || queryIndex != value.LastIndexOf(QueryPlaceholder, StringComparison.Ordinal))
        {
            throw new ArgumentException("Search template must contain exactly one {query} placeholder.", nameof(template));
        }

        var probe = value.Replace(QueryPlaceholder, "query");
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("Search template must be an absolute HTTP or HTTPS URL.", nameof(template));
        }

        return value;
    }

    private static bool IsNavigableScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "brezza", StringComparison.OrdinalIgnoreCase);

    public static bool IsInternalPage(Uri uri) =>
        uri is not null &&
        string.Equals(uri.Scheme, "brezza", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(uri.Host, "settings", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "history", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "bookmarks", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "newtab", StringComparison.OrdinalIgnoreCase));

    public static string GetCanonicalInternalUrl(Uri uri)
    {
        if (!IsInternalPage(uri)) throw new ArgumentException("The URI is not a supported internal page.", nameof(uri));
        return "brezza://" + uri.Host.ToLowerInvariant();
    }

    private static bool LooksLikeHost(string text)
    {
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            return text.IndexOf(']') >= 0;
        }

        var authorityEnd = text.IndexOfAny(new[] { '/', '?', '#' });
        var authority = authorityEnd < 0 ? text : text.Substring(0, authorityEnd);
        var hostEnd = authority.LastIndexOf(':');
        var host = hostEnd > 0 && authority.IndexOf(':') == hostEnd
            ? authority.Substring(0, hostEnd)
            : authority;

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host.IndexOf('.') >= 0;
    }
}
