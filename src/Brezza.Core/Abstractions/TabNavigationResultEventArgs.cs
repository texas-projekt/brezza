using System;

namespace Brezza.Core.Abstractions;

public sealed class TabNavigationResultEventArgs : EventArgs
{
    public TabNavigationResultEventArgs(bool isSuccess, string webErrorStatus, bool isSuperseded = false, bool isReplacedByDownload = false)
    {
        IsSuccess = isSuccess;
        WebErrorStatus = webErrorStatus;
        IsSuperseded = isSuperseded;
        IsReplacedByDownload = isReplacedByDownload;
    }

    public bool IsSuccess { get; }

    public string WebErrorStatus { get; }

    /// <summary>
    /// A newer navigation started before this one finished: a reload, a new address, back/forward,
    /// or the page navigating itself. Its outcome is replaced by the newer one, so it is not a
    /// failure of the page, whatever its error status.
    /// </summary>
    public bool IsSuperseded { get; }

    /// <summary>
    /// The navigation ended in a download instead of a page: the runtime aborts it once the
    /// download takes over. Like a superseded navigation, it is not a failure of the page.
    /// </summary>
    public bool IsReplacedByDownload { get; }
}
