using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace Brezza.App;

/// <summary>
/// Reads the browser's localized strings from <c>BrowserUi/locales</c>. The native shell keeps the
/// same language packs and language rules the previous chrome used: English and Simplified Chinese,
/// with a system-language default.
/// </summary>
internal sealed class Localization
{
    public const string English = "en";
    public const string Chinese = "zh-Hans";

    private readonly string _localesDirectory;
    private readonly JavaScriptSerializer _serializer = new();
    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    public Localization(string assetsDirectory)
    {
        _localesDirectory = Path.Combine(assetsDirectory, "locales");
        LoadLanguage(English);
    }

    /// <summary>The active language key, either <see cref="English"/> or <see cref="Chinese"/>.</summary>
    public string Language { get; private set; } = English;

    /// <summary>Loads a language pack, falling back to English when it cannot be read.</summary>
    public void LoadLanguage(string? language)
    {
        var normalized = Normalize(language);
        var loaded = Load(normalized);
        if (loaded is null && !string.Equals(normalized, English, StringComparison.Ordinal))
        {
            loaded = Load(English);
            normalized = English;
        }

        if (loaded is not null)
        {
            _strings = loaded;
            Language = normalized;
            return;
        }

        _strings = new Dictionary<string, string>(StringComparer.Ordinal);
        Language = English;
    }

    /// <summary>Maps any language value to a supported language key.</summary>
    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return English;
        return string.Equals(language!.Trim(), Chinese, StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;
    }

    /// <summary>The language the Windows UI culture selects when nothing is configured.</summary>
    public static string SystemLanguage() =>
        CultureInfo.InstalledUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;

    public string Get(string key)
    {
        return _strings.TryGetValue(key, out var value) ? value : key;
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
