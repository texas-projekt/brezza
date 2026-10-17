using System;

namespace Brezza.Core.Abstractions;

/// <summary>
/// How much of a running download has arrived. The runtime reports this often, so the shell
/// throttles what it shows; the record itself keeps only the last value.
/// </summary>
public sealed class DownloadProgressChangedEventArgs : EventArgs
{
    public DownloadProgressChangedEventArgs(string targetFilePath, ulong receivedBytes, ulong? totalBytes)
    {
        TargetFilePath = targetFilePath ?? string.Empty;
        ReceivedBytes = receivedBytes;
        TotalBytes = totalBytes;
    }

    public string TargetFilePath { get; }

    /// <summary>Bytes written so far.</summary>
    public ulong ReceivedBytes { get; }

    /// <summary>The total size the runtime reported, or null when it does not know it.</summary>
    public ulong? TotalBytes { get; }
}
