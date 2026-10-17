using System;
using Brezza.Core.Abstractions;
using Brezza.Core.Tabs;

namespace Brezza.Core.Downloads;

/// <summary>
/// One download the browser started, as browser state. The file name and target path come from the
/// adapter, which resolved them against the downloads folder; this record never touches the
/// filesystem itself.
/// </summary>
public sealed class BrowserDownload
{
    public BrowserDownload(
        string id,
        string sourceUrl,
        string fileName,
        string targetFilePath,
        BrowserDownloadState state,
        DateTime startedUtc,
        BrowserSessionKind sessionKind,
        DateTime? completedUtc = null,
        ulong? totalBytes = null,
        string? interruptReason = null,
        ulong? receivedBytes = null)
    {
        Id = id ?? string.Empty;
        SourceUrl = sourceUrl ?? string.Empty;
        FileName = fileName ?? string.Empty;
        TargetFilePath = targetFilePath ?? string.Empty;
        State = state;
        StartedUtc = startedUtc;
        CompletedUtc = completedUtc;
        TotalBytes = totalBytes;
        InterruptReason = interruptReason;
        ReceivedBytes = receivedBytes;
        SessionKind = sessionKind;
    }

    public string Id { get; }

    /// <summary>The page address the file came from.</summary>
    public string SourceUrl { get; }

    /// <summary>The sanitized file name the page offered for this download.</summary>
    public string FileName { get; }

    /// <summary>The local file the adapter writes the download to.</summary>
    public string TargetFilePath { get; }

    public BrowserDownloadState State { get; }

    public DateTime StartedUtc { get; }

    /// <summary>When the download reached a terminal state; null while it is in progress.</summary>
    public DateTime? CompletedUtc { get; }

    /// <summary>The total size the runtime reported, when it knows it.</summary>
    public ulong? TotalBytes { get; }

    /// <summary>Runtime interrupt reason text when the state is <see cref="BrowserDownloadState.Interrupted"/>.</summary>
    public string? InterruptReason { get; }

    /// <summary>Bytes written so far while the download is running, when the runtime reported any.</summary>
    public ulong? ReceivedBytes { get; }

    public BrowserSessionKind SessionKind { get; }

    /// <summary>A download that reached a terminal state and can be persisted.</summary>
    public bool IsTerminal => State is BrowserDownloadState.Completed or BrowserDownloadState.Interrupted;

    /// <summary>A download that is still running or paused, so its progress is worth showing.</summary>
    public bool IsRunning => State is BrowserDownloadState.InProgress or BrowserDownloadState.Paused;

    /// <summary>A private download lives in the session only and is never written to the store.</summary>
    public bool IsPrivate => SessionKind == BrowserSessionKind.Private;

    public BrowserDownload With(
        BrowserDownloadState state,
        DateTime completedUtc,
        ulong? totalBytes = null,
        string? interruptReason = null) =>
        new(Id, SourceUrl, FileName, TargetFilePath, state, StartedUtc, SessionKind, completedUtc, totalBytes, interruptReason, ReceivedBytes);

    /// <summary>The same download with a different live state, keeping its progress.</summary>
    public BrowserDownload WithState(BrowserDownloadState state) =>
        new(Id, SourceUrl, FileName, TargetFilePath, state, StartedUtc, SessionKind, CompletedUtc, TotalBytes, InterruptReason, ReceivedBytes);

    /// <summary>The same download with the bytes that have arrived so far.</summary>
    public BrowserDownload WithProgress(ulong receivedBytes) =>
        new(Id, SourceUrl, FileName, TargetFilePath, State, StartedUtc, SessionKind, CompletedUtc, TotalBytes, InterruptReason, receivedBytes);
}
