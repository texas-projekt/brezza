using System;
using System.Collections.Generic;

namespace Brezza.Core.Downloads;

/// <summary>Why a page-initiated download was refused.</summary>
public enum DownloadBlockReason
{
    TooManyConcurrent,
    TooLarge,
}

/// <summary>The result of asking for a slot in the download quota.</summary>
public readonly struct DownloadRequestDecision
{
    public DownloadRequestDecision(bool isAllowed, DownloadBlockReason? reason = null)
    {
        IsAllowed = isAllowed;
        Reason = reason;
    }

    public bool IsAllowed { get; }

    /// <summary>The reason for a refusal, or null when the request was allowed.</summary>
    public DownloadBlockReason? Reason { get; }
}

/// <summary>
/// Applies the browser-wide download guardrail: at most three downloads may be live at once and a
/// download whose total size is already known must not exceed eight GiB. A paused download keeps
/// its slot because the runtime is still holding its partial file. The limiter has no I/O and no
/// policy about where a file is written; the WebView2 adapter calls it before it starts an
/// operation and releases the slot when the runtime reports a terminal state.
/// </summary>
public sealed class DownloadRequestLimiter
{
    public const int MaximumConcurrent = 3;
    public const long MaximumBytes = 8L * 1024 * 1024 * 1024;

    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public int ActiveCount
    {
        get
        {
            lock (_gate) return _active.Count;
        }
    }

    /// <summary>
    /// Reserves a slot for <paramref name="targetFilePath"/>. A positive total over
    /// <see cref="MaximumBytes"/> is refused even when a slot is free; an unknown total is allowed
    /// so a server that does not send a length is not rejected up front.
    /// </summary>
    public DownloadRequestDecision TryAcquire(string targetFilePath, long? totalBytes)
    {
        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            throw new ArgumentException("A download needs a target path.", nameof(targetFilePath));
        }

        lock (_gate)
        {
            if (totalBytes is > MaximumBytes)
            {
                return new DownloadRequestDecision(false, DownloadBlockReason.TooLarge);
            }

            if (_active.Count >= MaximumConcurrent)
            {
                return new DownloadRequestDecision(false, DownloadBlockReason.TooManyConcurrent);
            }

            _active.Add(targetFilePath);
            return new DownloadRequestDecision(true);
        }
    }

    /// <summary>Releases a slot after completion, interruption, cancellation, or tab closure.</summary>
    public void Release(string targetFilePath)
    {
        if (string.IsNullOrWhiteSpace(targetFilePath)) return;
        lock (_gate) _active.Remove(targetFilePath);
    }
}
