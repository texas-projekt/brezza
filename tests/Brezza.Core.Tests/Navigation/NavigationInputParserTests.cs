using Brezza.Core.Navigation;

namespace Brezza.Core.Tests.Navigation;

public sealed class NavigationInputParserTests
{
    private const string Template = "https://search.example/?q={query}";

    [Theory]
    [InlineData("https://example.com/path", "https://example.com/path")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("localhost:8080/path", "https://localhost:8080/path")]
    [InlineData("[::1]:8080", "https://[::1]:8080/")]
    public void Parses_web_addresses(string input, string expected)
    {
        var target = new NavigationInputParser(Template).Parse(input);

        Assert.Equal(NavigationTargetKind.Url, target.Kind);
        Assert.Equal(expected, target.Uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("two words", "https://search.example/?q=two%20words")]
    [InlineData("javascript:alert(1)", "https://search.example/?q=javascript%3Aalert%281%29")]
    [InlineData("data:text/html,test", "https://search.example/?q=data%3Atext%2Fhtml%2Ctest")]
    public void Searches_non_addresses_and_unsafe_schemes(string input, string expected)
    {
        var target = new NavigationInputParser(Template).Parse(input);

        Assert.Equal(NavigationTargetKind.Search, target.Kind);
        Assert.Equal(expected, target.Uri!.AbsoluteUri);
    }

    [Fact]
    public void Empty_input_has_no_navigation_target()
    {
        Assert.Equal(NavigationTargetKind.Empty, new NavigationInputParser(Template).Parse("  ").Kind);
    }

    [Theory]
    [InlineData("brezza://settings")]
    [InlineData("brezza://history")]
    [InlineData("brezza://bookmarks")]
    public void Recognizes_canonical_internal_pages(string input)
    {
        var target = new NavigationInputParser(Template).Parse(input);

        Assert.Equal(NavigationTargetKind.Url, target.Kind);
        Assert.True(NavigationInputParser.IsInternalPage(target.Uri!));
        Assert.Equal(input, NavigationInputParser.GetCanonicalInternalUrl(target.Uri!));
    }

    [Theory]
    [InlineData("https://search.example/?q=query")]
    [InlineData("javascript:alert('{query}')")]
    [InlineData("https://search.example/?q={query}&again={query}")]
    public void Rejects_invalid_search_templates(string template)
    {
        Assert.Throws<ArgumentException>(() => new NavigationInputParser(template));
    }
}
