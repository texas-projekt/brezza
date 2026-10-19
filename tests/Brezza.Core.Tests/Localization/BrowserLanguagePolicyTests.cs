using Brezza.Core.Localization;

namespace Brezza.Core.Tests.Localization;

public sealed class BrowserLanguagePolicyTests
{
    [Theory]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hant-TW", "zh-Hant")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData("", "en")]
    public void A_culture_maps_to_the_pack_that_can_display_it(string culture, string expected)
    {
        Assert.Equal(expected, BrowserLanguagePolicy.Normalize(culture));
    }

    [Fact]
    public void A_traditional_culture_is_not_written_back_as_a_country_tag()
    {
        Assert.Equal("zh-Hant", BrowserLanguagePolicy.FromCulture("zh-TW"));
        Assert.Equal("zh-Hant", BrowserLanguagePolicy.FromCulture("zh-HK"));
    }

    [Fact]
    public void An_unknown_stored_value_is_not_silently_saved_as_English()
    {
        Assert.True(BrowserLanguagePolicy.TryNormalize("zh-HK", out var traditional));
        Assert.Equal("zh-Hant", traditional);

        Assert.False(BrowserLanguagePolicy.TryNormalize("fr", out _));
        Assert.False(BrowserLanguagePolicy.TryNormalize(string.Empty, out _));
    }
}
