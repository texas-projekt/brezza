using System.Xml.Serialization;

namespace Brezza.Persistence;

[XmlRoot("settings")]
public sealed class BrowserSettings
{
    public const int CurrentSchemaVersion = 1;

    [XmlAttribute("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [XmlElement("searchTemplate")]
    public string SearchTemplate { get; set; } = "https://www.bing.com/search?q={query}";

    [XmlElement("language")]
    public string Language { get; set; } = string.Empty;

    public static string NormalizeLanguage(string? language)
    {
        if (string.Equals(language, "zh-Hans", System.StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
        if (string.Equals(language, "en", System.StringComparison.OrdinalIgnoreCase)) return "en";
        return string.Empty;
    }
}
