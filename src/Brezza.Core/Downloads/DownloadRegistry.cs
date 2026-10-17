using System;
using System.Collections.Generic;
using System.Linq;
using Brezza.Core.Abstractions;
using Brezza.Core.Tabs;

namespace Brezza.Core.Downloads;

/// <summary>
/// The browser's list of downloads: the source of truth for what was downloaded, what is still
/// running, and what failed. It owns the state transitions the runtime reports and keeps the
/// newest download first. It performs no filesystem I/O — the adapter resolves the target file and
/// the store persists the records. The live list is not capped; the store keeps only the newest
/// records when it writes.
/// </summary>
public sealed class DownloadRegistry
{
    // A terminal status can be reported for a download whose start has not been observed yet. The
    // status is kept until the start arrives, so a completion is never lost to the event order.
    // Paths are compared the way the list below compares them: a Windows path is not case sensitive.
    private readonly Dictionary<string, PendingStatus> _pendingStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BrowserDownload> _downloads = new();

    /// <summary>How many unobserved terminal statuses are kept before they are all dropped.</summary>
    private const int MaximumPendingStatuses = 32;

    public event EventHandler? Changed;

    /// <summary>The known downloads, newest first.</summary>
    public IReadOnlyList<BrowserDownload> Downloads
    {
        get
        {
            lock (_downloads)
            {
                return _downloads.ToArray();
            }
        }
    }

    /// <summary>Records a download that started and was given a local target file.</summary>
    public BrowserDownload Start(string sourceUrl, string fileName, string targetFilePath, BrowserSessionKind sessionKind)
    {
        var download = new BrowserDownload(
            "download-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            sourceUrl,
            fileName,
            targetFilePath,
            BrowserDownloadState.InProgress,
            DateTime.UtcNow,
            sessionKind);

        lock (_downloads)
        {
            _downloads.Insert(0, download);
            if (_pendingStatuses.Count > 0 && _pendingStatuses.TryGetValue(targetFilePath, out var pending))
            {
                _pendingStatuses.Remove(targetFilePath);
                download = ApplyStatus(download, pending);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return download;
    }

    /// <summary>Records that the download written to <paramref name="targetFilePath"/> completed.</summary>
    public BrowserDownload? Complete(string targetFilePath, ulong? totalBytes)
    {
        return ReportTerminal(targetFilePath, new PendingStatus(BrowserDownloadState.Completed, totalBytes, null));
    }

    /// <summary>Records that the download written to <paramref name="targetFilePath"/> was interrupted.</summary>
    public BrowserDownload? Interrupt(string targetFilePath, string? interruptReason)
    {
        return ReportTerminal(targetFilePath, new PendingStatus(BrowserDownloadState.Interrupted, null, interruptReason));
    }

    /// <summary>Records that the download written to <paramref name="targetFilePath"/> was paused.</summary>
    public BrowserDownload? Pause(string targetFilePath) => SetLiveState(targetFilePath, BrowserDownloadState.Paused);

    /// <summary>Records that a paused download is running again.</summary>
    public BrowserDownload? Resume(string targetFilePath) => SetLiveState(targetFilePath, BrowserDownloadState.InProgress);

    /// <summary>Records how many bytes of the download have arrived.</summary>
    public BrowserDownload? ReportProgress(string targetFilePath, ulong receivedBytes)
    {
        BrowserDownload? updated = null;
        lock (_downloads)
        {
            var index = FindIndex(targetFilePath);
            if (index >= 0)
            {
                updated = _downloads[index].WithProgress(receivedBytes);
                _downloads[index] = updated;
            }
        }

        if (updated is not null) Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>
    /// Adds a download restored from the local store. Entries are appended in the order they are
    /// given, so a store that hands them newest first — as <see cref="Persistence.DownloadStore"/>
    /// does — keeps the list newest first.
    /// </summary>
    public void Restore(BrowserDownload download)
    {
        if (download is null) throw new ArgumentNullException(nameof(download));
        lock (_downloads)
        {
            _downloads.Add(download);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string downloadId)
    {
        lock (_downloads)
        {
            var index = _downloads.FindIndex(item => string.Equals(item.Id, downloadId, StringComparison.Ordinal));
            if (index < 0) return false;
            _downloads.RemoveAt(index);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        lock (_downloads)
        {
            _downloads.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Drops every private-session download. A private download is listed while its session is
    /// open and is never persisted, so ending the session ends its list entries as well.
    /// </summary>
    public int RemovePrivateSession()
    {
        int removed;
        lock (_downloads)
        {
            removed = _downloads.RemoveAll(item => item.IsPrivate);
        }

        // The event is raised only when a private entry was actually dropped.
        if (removed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    private BrowserDownload? SetLiveState(string targetFilePath, BrowserDownloadState state)
    {
        BrowserDownload? updated = null;
        lock (_downloads)
        {
            var index = FindIndex(targetFilePath);
            if (index >= 0)
            {
                updated = _downloads[index].WithState(state);
                _downloads[index] = updated;
            }
        }

        if (updated is not null) Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>Finds a download by the file it is written to. Caller holds the list lock.</summary>
    private int FindIndex(string targetFilePath) =>
        _downloads.FindIndex(item => string.Equals(item.TargetFilePath, targetFilePath, StringComparison.OrdinalIgnoreCase));

    private BrowserDownload? ReportTerminal(string targetFilePath, PendingStatus status)
    {
        BrowserDownload? updated = null;
        lock (_downloads)
        {
            var index = FindIndex(targetFilePath);
            if (index >= 0)
            {
                updated = ApplyStatus(_downloads[index], status);
                _downloads[index] = updated;
            }
            else if (_pendingStatuses.Count >= MaximumPendingStatuses)
            {
                _pendingStatuses.Clear();
            }

            if (index < 0) _pendingStatuses[targetFilePath] = status;
        }

        if (updated is not null) Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    private static BrowserDownload ApplyStatus(BrowserDownload download, PendingStatus status) =>
        download.With(status.State, DateTime.UtcNow, status.TotalBytes, status.InterruptReason);

    private readonly struct PendingStatus
    {
        public PendingStatus(BrowserDownloadState state, ulong? totalBytes, string? interruptReason)
        {
            State = state;
            TotalBytes = totalBytes;
            InterruptReason = interruptReason;
        }

        public BrowserDownloadState State { get; }

        public ulong? TotalBytes { get; }

        public string? InterruptReason { get; }
    }
}
