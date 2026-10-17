using System;

namespace Brezza.Core.Abstractions;

/// <summary>State of a download the browser started.</summary>
public enum BrowserDownloadState
{
    InProgress,
    Completed,
    Interrupted,

    /// <summary>
    /// A running download the user paused. It is not a finished record, so it is never persisted.
    /// </summary>
    Paused,
}

/// <summary>A download changed state, so the shell can report success or the failure reason.</summary>
public sealed class DownloadStatusChangedEventArgs : EventArgs
{
    public DownloadStatusChangedEventArgs(
        BrowserDownloadState state,
        string targetFilePath,
        string sourceUrl,
        ulong? totalBytes,
        string? interruptReason)
    {
        State = state;
        TargetFilePath = targetFilePath ?? string.Empty;
        SourceUrl = sourceUrl ?? string.Empty;
        TotalBytes = totalBytes;
        InterruptReason = interruptReason;
    }

    public BrowserDownloadState State { get; }

    public string TargetFilePath { get; }

    public string SourceUrl { get; }

    public ulong? TotalBytes { get; }

    /// <summary>Runtime interrupt reason text when the state is <see cref="BrowserDownloadState.Interrupted"/>.</summary>
    public string? InterruptReason { get; }
}
