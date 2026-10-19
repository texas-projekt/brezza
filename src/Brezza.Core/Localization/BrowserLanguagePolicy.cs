using System;
using System.Collections.Generic;

namespace Brezza.Core.Localization;

/// <summary>
/// Maps culture and stored language values to the language packs the browser ships. Traditional
/// Chinese uses the script subtag <c>zh-Hant</c>; the legacy country tags <c>zh-TW</c>,
/// <c>zh-HK</c>, and <c>zh-MO</c> are recognized as aliases, never written back as stored values.
/// A value with no matching pack falls back to English.
/// </summary>
public static class BrowserLanguagePolicy
{
    public const string English = "en";
    public const string SimplifiedChinese = "zh-Hans";
    public const string TraditionalChinese = "zh-Hant";

    public static IReadOnlyList<string> SupportedLanguages { get; } =
        new[] { English, SimplifiedChinese, TraditionalChinese };

    /// <summary>Normalizes a stored setting or culture name to a supported pack key.</summary>
    public static string Normalize(string? language) =>
        TryNormalize(language, out var normalized) ? normalized : English;

    /// <summary>
    /// Normalizes a value only when it names a supported language. Unlike
    /// <see cref="Normalize(string?)"/>, an unknown value is not silently turned into English; a
    /// stored setting uses this to fall back to the system default instead.
    /// </summary>
    public static bool TryNormalize(string? language, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(language)) return false;
        var value = language!.Trim().Replace('_', '-');
        if (value.Equals(TraditionalChinese, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("zh-Hant-", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("zh-MO", StringComparison.OrdinalIgnoreCase))
        {
            normalized = TraditionalChinese;
            return true;
        }

        if (value.Equals(SimplifiedChinese, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("zh-Hans-", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("zh-SG", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            normalized = SimplifiedChinese;
            return true;
        }

        if (value.Equals(English, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
        {
            normalized = English;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The pack selected by the Windows UI culture when the user has not chosen a language.
    /// Chinese cultures without a script or region are treated as Simplified, matching Windows'
    /// default for the neutral culture.
    /// </summary>
    public static string FromCulture(string? cultureName) => Normalize(cultureName);
}
