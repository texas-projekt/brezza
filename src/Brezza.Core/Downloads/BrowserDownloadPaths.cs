using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Brezza.Core.Downloads;

/// <summary>
/// Resolves where a download is written. The file name comes from the page, so it is sanitized,
/// length-limited, and de-duplicated instead of being trusted.
/// </summary>
public static class BrowserDownloadPaths
{
    public const string FallbackFileName = "download";

    /// <summary>
    /// Keeps the complete path comfortably below the legacy 260-character limit even when the
    /// downloads folder is long. The extension is preserved.
    /// </summary>
    public const int MaxFileNameLength = 120;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

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
        var cleaned = builder.ToString().Trim().TrimStart('.').TrimEnd('.', ' ');
        if (cleaned.Length == 0) return FallbackFileName;

        var extension = Path.GetExtension(cleaned);
        var stem = Path.GetFileNameWithoutExtension(cleaned).TrimEnd(' ', '.');
        if (stem.Length == 0) return FallbackFileName;

        // CON, NUL, COM1, and friends name devices even when an extension follows them.
        if (ReservedNames.Contains(stem)) stem = "_" + stem;
        return Fit(stem, extension);
    }

    /// <summary>Truncates the stem so the name stays under the limit while keeping the extension.</summary>
    private static string Fit(string stem, string extension)
    {
        if (extension.Length > 16) extension = extension.Substring(0, 16);
        var maximumStem = MaxFileNameLength - extension.Length;
        if (maximumStem < 1)
        {
            extension = string.Empty;
            maximumStem = MaxFileNameLength;
        }

        if (stem.Length > maximumStem) stem = stem.Substring(0, maximumStem).TrimEnd(' ', '.');
        return stem.Length == 0 ? FallbackFileName : stem + extension;
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
            var suffix = " (" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            var maximumStem = Math.Max(1, MaxFileNameLength - extension.Length - suffix.Length);
            var trimmedStem = stem.Length > maximumStem ? stem.Substring(0, maximumStem).TrimEnd(' ', '.') : stem;
            candidate = Path.Combine(folder, trimmedStem + suffix + extension);
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
                string decoded;
                try
                {
                    decoded = Uri.UnescapeDataString(segment);
                }
                catch (UriFormatException)
                {
                    // A malformed escape sequence is a bad page-provided name, not a crash.
                    return FallbackFileName;
                }

                var sanitized = SanitizeFileName(decoded);
                if (!string.Equals(sanitized, FallbackFileName, StringComparison.OrdinalIgnoreCase)) return sanitized;
            }
        }

        return FallbackFileName;
    }
}
