using System.IO;
using System.Xml.Serialization;
using Brezza.Core.Localization;
using Brezza.Core.Navigation;

namespace Brezza.Persistence;

[XmlRoot("settings")]
public sealed class BrowserSettings
{
    public const int CurrentSchemaVersion = 4;

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

    /// <summary>
    /// The folder downloads are written to. Empty means the user's own Downloads folder, which is
    /// what the browser uses until another one is picked; the value is a full path.
    /// </summary>
    [XmlElement("downloadsFolder")]
    public string DownloadsFolder { get; set; } = string.Empty;

    public static string NormalizeLanguage(string? language) =>
        BrowserLanguagePolicy.TryNormalize(language, out var normalized) ? normalized : string.Empty;

    public static BrowserThemeMode NormalizeTheme(BrowserThemeMode theme) =>
        theme is BrowserThemeMode.Light or BrowserThemeMode.Dark
            ? theme
            : BrowserThemeMode.System;

    public static BrowserStartupBehavior NormalizeStartupBehavior(BrowserStartupBehavior behavior) =>
        behavior == BrowserStartupBehavior.RestoreSession
            ? BrowserStartupBehavior.RestoreSession
            : BrowserStartupBehavior.NewTab;

    /// <summary>
    /// Normalizes the downloads folder to a full path. An empty, whitespace, or unparsable value
    /// falls back to empty, which is the user's own Downloads folder rather than a folder the
    /// browser would fail to write to.
    /// </summary>
    public static string NormalizeDownloadsFolder(string? folder)
    {
        var value = (folder ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is System.ArgumentException or System.NotSupportedException or System.IO.PathTooLongException)
        {
            return string.Empty;
        }
    }
}
