using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Brezza.Core.Downloads;

/// <summary>
/// Resolves where a download is written. The file name comes from the page, so it is sanitized and
/// de-duplicated instead of being trusted.
/// </summary>
public static class BrowserDownloadPaths
{
    public const string FallbackFileName = "download";

    public static string SanitizeFileName(string? suggestedFileName)
    {
        var name = (suggestedFileName ?? string.Empty).Trim();
        if (name.Length == 0) return FallbackFileName;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (invalid.Contains(character) || character == '/' || character == '\\') continue;
            builder.Append(character);
        }

        // A leading dot would create a hidden or relative file name.
        var cleaned = builder.ToString().Trim().TrimStart('.').TrimEnd('.');
        return cleaned.Length == 0 ? FallbackFileName : cleaned;
    }

    /// <summary>Returns a free file path in <paramref name="downloadsFolder"/> for a suggested name.</summary>
    public static string ResolveTargetPath(string downloadsFolder, string? suggestedFileName, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(downloadsFolder)) throw new ArgumentException("A downloads folder is required.", nameof(downloadsFolder));
        if (fileExists is null) throw new ArgumentNullException(nameof(fileExists));
        var folder = downloadsFolder;
        var name = SanitizeFileName(suggestedFileName);
        var candidate = Path.Combine(folder, name);
        if (!fileExists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 1; index < 1000; index++)
        {
            candidate = Path.Combine(folder, stem + " (" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" + extension);
            if (!fileExists(candidate)) return candidate;
        }

        return Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
    }

    /// <summary>Picks the file name a page offered for a download, from its disposition or URL.</summary>
    public static string SuggestedFileNameFrom(string? contentDisposition, string? sourceUrl)
    {
        if (contentDisposition is not null && contentDisposition.Length > 0)
        {
            const string marker = "filename=";
            var index = contentDisposition.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var value = contentDisposition.Substring(index + marker.Length).Trim().Trim('"');
                var separator = value.IndexOf(';');
                if (separator >= 0) value = value.Substring(0, separator);
                var sanitized = SanitizeFileName(value);
                if (!string.Equals(sanitized, FallbackFileName, StringComparison.OrdinalIgnoreCase)) return sanitized;
            }
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            var segment = uri.Segments.LastOrDefault()?.Trim('/');
            if (segment is not null && segment.Length > 0)
            {
                var sanitized = SanitizeFileName(Uri.UnescapeDataString(segment));
                if (!string.Equals(sanitized, FallbackFileName, StringComparison.OrdinalIgnoreCase)) return sanitized;
            }
        }

        return FallbackFileName;
    }
}
