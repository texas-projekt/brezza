using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Brezza.Core.Tests.Localization;

public sealed class LanguagePackTests
{
    private static readonly Regex Placeholder = new(@"\{[A-Za-z]+\}");

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void A_translated_pack_has_the_same_keys_and_placeholders_as_English(string language)
    {
        var english = Load("en");
        var translated = Load(language);

        Assert.Empty(english.Keys.Except(translated.Keys));
        Assert.Empty(translated.Keys.Except(english.Keys));
        foreach (var key in english.Keys)
        {
            Assert.True(Placeholders(english[key]).SequenceEqual(Placeholders(translated[key])),
                language + " has different placeholders for '" + key + "'.");
        }
    }

    private static Dictionary<string, string> Load(string language)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "locales", language + ".json");
        return new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
    }

    private static IEnumerable<string> Placeholders(string text) =>
        Placeholder.Matches(text).Cast<Match>().Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal);
}
