using System.Xml.Serialization;
using Brezza.Core.Navigation;

namespace Brezza.Persistence;

[XmlRoot("settings")]
public sealed class BrowserSettings
{
    public const int CurrentSchemaVersion = 3;

    [XmlAttribute("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [XmlElement("searchTemplate")]
    public string SearchTemplate { get; set; } = "https://www.bing.com/search?q={query}";

    [XmlElement("language")]
    public string Language { get; set; } = string.Empty;

    [XmlElement("theme")]
    public BrowserThemeMode Theme { get; set; } = BrowserThemeMode.System;

    /// <summary>What the browser opens at startup; the new-tab page is the default.</summary>
    [XmlElement("startupBehavior")]
    public BrowserStartupBehavior StartupBehavior { get; set; } = BrowserStartupBehavior.NewTab;

    public static string NormalizeLanguage(string? language)
    {
        if (string.Equals(language, "zh-Hans", System.StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
        if (string.Equals(language, "en", System.StringComparison.OrdinalIgnoreCase)) return "en";
        return string.Empty;
    }

    public static BrowserThemeMode NormalizeTheme(BrowserThemeMode theme) =>
        theme is BrowserThemeMode.Light or BrowserThemeMode.Dark
            ? theme
            : BrowserThemeMode.System;

    public static BrowserStartupBehavior NormalizeStartupBehavior(BrowserStartupBehavior behavior) =>
        behavior == BrowserStartupBehavior.RestoreSession
            ? BrowserStartupBehavior.RestoreSession
            : BrowserStartupBehavior.NewTab;
}
