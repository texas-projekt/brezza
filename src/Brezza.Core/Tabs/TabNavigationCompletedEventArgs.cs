using System;

namespace Brezza.Core.Tabs;

public sealed class TabNavigationCompletedEventArgs : EventArgs
{
    public TabNavigationCompletedEventArgs(BrowserTab tab, string url, string title, bool isSuccess, string error)
    {
        Tab = tab;
        Url = url;
        Title = title;
        IsSuccess = isSuccess;
        Error = error;
    }

    public BrowserTab Tab { get; }

    public string Url { get; }

    public string Title { get; }

    public bool IsSuccess { get; }

    public string Error { get; }
}
