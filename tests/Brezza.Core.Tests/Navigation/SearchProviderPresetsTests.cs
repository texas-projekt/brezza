using Brezza.Core.Navigation;
using Xunit;

namespace Brezza.Core.Tests.Navigation;

public class SearchProviderPresetsTests
{
    [Fact]
    public void EveryPresetIsAValidSearchTemplate()
    {
        foreach (var preset in SearchProviderPresets.All)
        {
            Assert.Equal(preset.Template, NavigationInputParser.ValidateTemplate(preset.Template));
        }
    }

    [Fact]
    public void EveryPresetUsesExactlyOneQueryPlaceholder()
    {
        foreach (var preset in SearchProviderPresets.All)
        {
            Assert.Equal(1, CountOccurrences(preset.Template, NavigationInputParser.QueryPlaceholder));
        }
    }

    [Fact]
    public void EveryPresetIsHttpOrHttps()
    {
        foreach (var preset in SearchProviderPresets.All)
        {
            Assert.True(Uri.TryCreate(preset.Template.Replace(NavigationInputParser.QueryPlaceholder, "query"), UriKind.Absolute, out var uri));
            Assert.True(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps, preset.Name + " is not an HTTP(S) template.");
        }
    }

    [Fact]
    public void PresetNamesAreUnique()
    {
        Assert.Equal(
            SearchProviderPresets.All.Count,
            SearchProviderPresets.All.Select(preset => preset.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FindMatchesAKnownTemplateIgnoringCase()
    {
        var preset = SearchProviderPresets.Find("HTTPS://WWW.BING.COM/SEARCH?Q={query}");
        Assert.NotNull(preset);
        Assert.Equal("Bing", preset!.Name);
    }

    [Fact]
    public void FindReturnsNullForAnUnknownOrEmptyTemplate()
    {
        Assert.Null(SearchProviderPresets.Find("https://example.test/search?q={query}"));
        Assert.Null(SearchProviderPresets.Find(null));
        Assert.Null(SearchProviderPresets.Find(string.Empty));
    }

    [Fact]
    public void APresetTemplateResolvesASearchQuery()
    {
        var preset = SearchProviderPresets.Find("https://duckduckgo.com/?q={query}");
        Assert.NotNull(preset);
        var parser = new NavigationInputParser(preset!.Template);
        var target = parser.Parse("brezza native ui");
        Assert.Equal(NavigationTargetKind.Search, target.Kind);
        Assert.Contains("brezza%20native%20ui", target.Uri!.AbsoluteUri, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
