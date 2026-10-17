namespace Brezza.Core.Privacy;

/// <summary>Outcome of clearing one kind of browsing data.</summary>
public enum BrowsingDataClearState
{
    /// <summary>The runtime cleared the data.</summary>
    Completed,

    /// <summary>The clear failed; the failure detail is on the result.</summary>
    Failed,
}

/// <summary>
/// Observable result of a browsing-data clear. A task that clears profile data is never discarded
/// silently: the shell reports this result, so a clear that fails is visible instead of lost.
/// </summary>
public sealed class BrowsingDataClearResult
{
    public BrowsingDataClearResult(BrowsingDataClearState state, string? detail)
    {
        State = state;
        Detail = detail;
    }

    public BrowsingDataClearState State { get; }

    /// <summary>Last failure message when the clear failed.</summary>
    public string? Detail { get; }

    public bool IsComplete => State == BrowsingDataClearState.Completed;
}
