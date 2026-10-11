using System;
using System.IO;
using System.Xml.Serialization;

namespace Brezza.Persistence;

internal static class AtomicXmlFile
{
    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return new XmlSerializer(typeof(T)).Deserialize(stream) as T;
        }
        catch (Exception exception) when (
            exception is IOException || exception is InvalidOperationException || exception is ArgumentException)
        {
            return null;
        }
    }

    public static void Save<T>(string path, T document)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new XmlSerializer(typeof(T)).Serialize(stream, document);
                stream.Flush(true);
            }

            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
