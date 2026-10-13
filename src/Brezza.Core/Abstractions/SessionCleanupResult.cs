namespace Brezza.Core.Abstractions;

/// <summary>Outcome of removing one browser session's local data.</summary>
public enum SessionCleanupState
{
    /// <summary>The session data directory was removed and its absence was verified.</summary>
    Completed,

    /// <summary>The directory still exists after the bounded attempt budget; the failure is retryable.</summary>
    Pending,

    /// <summary>The directory still exists after a failure that retrying is unlikely to fix.</summary>
    Failed,
}

/// <summary>
/// Observable result of a session cleanup attempt. A result that is not <see cref="Completed"/>
/// keeps its <see cref="DataFolder"/> so the caller can retry and report it.
/// </summary>
public sealed class SessionCleanupResult
{
    public SessionCleanupResult(
        Brezza.Core.Tabs.BrowserSessionKind sessionKind,
        SessionCleanupState state,
        string dataFolder,
        int attempts,
        string? detail)
    {
        SessionKind = sessionKind;
        State = state;
        DataFolder = dataFolder ?? string.Empty;
        Attempts = attempts;
        Detail = detail;
    }

    public Brezza.Core.Tabs.BrowserSessionKind SessionKind { get; }

    public SessionCleanupState State { get; }

    /// <summary>The session data folder this result refers to; empty when there was nothing to remove.</summary>
    public string DataFolder { get; }

    /// <summary>How many deletion attempts were made for the folder.</summary>
    public int Attempts { get; }

    /// <summary>Last failure message, when the folder could not be removed.</summary>
    public string? Detail { get; }

    public bool IsComplete => State == SessionCleanupState.Completed;
}
