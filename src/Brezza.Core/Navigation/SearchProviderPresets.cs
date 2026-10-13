using System;
using System.Collections.Generic;
using System.Linq;

namespace Brezza.Core.Navigation;

/// <summary>A selectable search provider offered in browser settings.</summary>
public sealed class SearchProviderPreset
{
    public SearchProviderPreset(string name, string template)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Template = template ?? throw new ArgumentNullException(nameof(template));
    }

    /// <summary>Provider name. Brand names are not translated.</summary>
    public string Name { get; }

    /// <summary>Search URL template containing exactly one {query} placeholder.</summary>
    public string Template { get; }
}

/// <summary>
/// The search providers Brezza offers. Core owns the list so the native settings dialog and
/// persistence share one definition; the adapter only displays it.
/// </summary>
public static class SearchProviderPresets
{
    public static IReadOnlyList<SearchProviderPreset> All { get; } = new[]
    {
        new SearchProviderPreset("Bing", "https://www.bing.com/search?q={query}"),
        new SearchProviderPreset("Google", "https://www.google.com/search?q={query}"),
        new SearchProviderPreset("DuckDuckGo", "https://duckduckgo.com/?q={query}"),
        new SearchProviderPreset("Baidu", "https://www.baidu.com/s?wd={query}"),
    };

    /// <summary>The preset a configured template selects, or null when it is a custom template.</summary>
    public static SearchProviderPreset? Find(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        return All.FirstOrDefault(preset =>
            string.Equals(preset.Template, template, StringComparison.OrdinalIgnoreCase));
    }
}
