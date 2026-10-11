using System;

namespace Brezza.Core.Abstractions;

public sealed class TabNavigationResultEventArgs : EventArgs
{
    public TabNavigationResultEventArgs(bool isSuccess, string webErrorStatus)
    {
        IsSuccess = isSuccess;
        WebErrorStatus = webErrorStatus;
    }

    public bool IsSuccess { get; }

    public string WebErrorStatus { get; }
}
