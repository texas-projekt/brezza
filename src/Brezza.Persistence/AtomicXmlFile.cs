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
        catch (IOException)
        {
            // A file that is temporarily unreadable (another process holds it) is not corrupt:
            // leave it in place so the next load can read it instead of destroying it.
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException || exception is ArgumentException)
        {
            Quarantine(path, exception);
            return null;
        }
    }

    /// <summary>
    /// Moves a file the serializer cannot read out of the way, so a later save cannot silently
    /// overwrite the only copy of the user's data. Best effort: a failed move is reported and the
    /// original remains where it was.
    /// </summary>
    internal static void Quarantine(string path, Exception exception)
    {
        var quarantinePath = path + ".corrupt-" + DateTime.UtcNow.Ticks;
        try
        {
            File.Move(path, quarantinePath);
            System.Diagnostics.Trace.TraceWarning(
                "A corrupt data file was moved aside: {0} -> {1} ({2})",
                path, quarantinePath, exception.Message);
        }
        catch (Exception moveException) when (
            moveException is IOException || moveException is UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError(
                "A corrupt data file could not be moved aside: {0} ({1})",
                path, moveException.Message);
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
