using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using Brezza.Core.Localization;

namespace Brezza.App;

/// <summary>
/// Reads the browser's localized strings from <c>BrowserUi/locales</c>. The shell ships English,
/// Simplified Chinese, and Traditional Chinese packs, with a system-language default.
/// </summary>
internal sealed class Localization
{
    public const string English = BrowserLanguagePolicy.English;
    public const string Chinese = BrowserLanguagePolicy.SimplifiedChinese;
    public const string TraditionalChinese = BrowserLanguagePolicy.TraditionalChinese;

    private readonly string _localesDirectory;
    private readonly JavaScriptSerializer _serializer = new();
    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    public Localization(string assetsDirectory)
    {
        _localesDirectory = Path.Combine(assetsDirectory, "locales");
        LoadLanguage(English);
    }

    /// <summary>The active language key: English, Simplified Chinese, or Traditional Chinese.</summary>
    public string Language { get; private set; } = English;

    /// <summary>Loads a language pack, falling back to English when it cannot be read.</summary>
    public void LoadLanguage(string? language)
    {
        var normalized = Normalize(language);
        var fallback = Load(English);
        var loaded = string.Equals(normalized, English, StringComparison.Ordinal)
            ? fallback
            : Load(normalized);
        if (loaded is null)
        {
            loaded = fallback;
            normalized = English;
        }

        _strings = loaded ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _fallback = fallback ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Language = normalized;
    }

    /// <summary>Maps any language value to a supported language key.</summary>
    public static string Normalize(string? language) => BrowserLanguagePolicy.Normalize(language);

    /// <summary>The language the Windows UI culture selects when nothing is configured.</summary>
    public static string SystemLanguage() =>
        BrowserLanguagePolicy.FromCulture(CultureInfo.InstalledUICulture.Name);

    public string Get(string key)
    {
        if (_strings.TryGetValue(key, out var value)) return value;
        // A key missing from a translated pack falls back to English instead of showing the key.
        return _fallback.TryGetValue(key, out var fallback) ? fallback : key;
    }

    /// <summary>Gets a string and replaces its <c>{name}</c> placeholder.</summary>
    public string Format(string key, string name, object? value)
    {
        var text = Get(key);
        return value is null ? text.Replace("{" + name + "}", string.Empty) : text.Replace("{" + name + "}", value.ToString());
    }

    private Dictionary<string, string>? Load(string language)
    {
        var path = Path.Combine(_localesDirectory, language + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var parsed = _serializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null) return null;
            return new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException || exception is ArgumentException || exception is InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("The {0} language pack could not be read: {1}", language, exception.Message);
            return null;
        }
    }
}
