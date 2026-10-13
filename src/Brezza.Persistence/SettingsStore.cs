using System;
using System.IO;
using System.Xml.Serialization;
using Brezza.Core.Navigation;

namespace Brezza.Persistence;

public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public BrowserSettings Load()
    {
        if (!File.Exists(_path)) return new BrowserSettings();

        try
        {
            using var stream = File.OpenRead(_path);
            if (new XmlSerializer(typeof(BrowserSettings)).Deserialize(stream) is not BrowserSettings settings ||
                (settings.SchemaVersion != BrowserSettings.CurrentSchemaVersion && settings.SchemaVersion != 1))
            {
                return new BrowserSettings();
            }

            settings.SearchTemplate = NavigationInputParser.ValidateTemplate(settings.SearchTemplate);
            settings.Language = BrowserSettings.NormalizeLanguage(settings.Language);
            settings.Theme = BrowserSettings.NormalizeTheme(settings.Theme);
            settings.SchemaVersion = BrowserSettings.CurrentSchemaVersion;
            return settings;
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidOperationException || exception is ArgumentException)
        {
            return new BrowserSettings();
        }
    }

    public void Save(BrowserSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (settings.SchemaVersion != BrowserSettings.CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported settings schema version.", nameof(settings));
        }

        settings.SearchTemplate = NavigationInputParser.ValidateTemplate(settings.SearchTemplate);
        settings.Language = BrowserSettings.NormalizeLanguage(settings.Language);
        settings.Theme = BrowserSettings.NormalizeTheme(settings.Theme);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new XmlSerializer(typeof(BrowserSettings)).Serialize(stream, settings);
                stream.Flush(true);
            }

            if (File.Exists(_path)) File.Replace(temporaryPath, _path, null);
            else File.Move(temporaryPath, _path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
