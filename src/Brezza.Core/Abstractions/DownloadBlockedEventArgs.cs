using System;
using Brezza.Core.Downloads;

namespace Brezza.Core.Abstractions;

/// <summary>A page asked for a download the browser's quota refused.</summary>
public sealed class DownloadBlockedEventArgs : EventArgs
{
    public DownloadBlockedEventArgs(
        string sourceUrl,
        string suggestedFileName,
        DownloadBlockReason reason,
        long? totalBytes)
    {
        SourceUrl = sourceUrl ?? string.Empty;
        SuggestedFileName = suggestedFileName ?? string.Empty;
        Reason = reason;
        TotalBytes = totalBytes;
    }

    public string SourceUrl { get; }

    public string SuggestedFileName { get; }

    public DownloadBlockReason Reason { get; }

    /// <summary>The size the runtime reported before the download started, when it did.</summary>
    public long? TotalBytes { get; }
}
