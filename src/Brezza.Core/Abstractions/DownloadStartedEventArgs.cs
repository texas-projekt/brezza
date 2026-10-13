using System;

namespace Brezza.Core.Abstractions;

/// <summary>A download started in a tab and was given a local target file.</summary>
public sealed class DownloadStartedEventArgs : EventArgs
{
    public DownloadStartedEventArgs(string sourceUrl, string suggestedFileName, string mimeType, string targetFilePath)
    {
        SourceUrl = sourceUrl ?? string.Empty;
        SuggestedFileName = suggestedFileName ?? string.Empty;
        MimeType = mimeType ?? string.Empty;
        TargetFilePath = targetFilePath ?? string.Empty;
    }

    public string SourceUrl { get; }

    public string SuggestedFileName { get; }

    public string MimeType { get; }

    public string TargetFilePath { get; }
}
