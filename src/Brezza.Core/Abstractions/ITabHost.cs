using System;
using System.Threading.Tasks;

namespace Brezza.Core.Abstractions;

public interface ITabHost : IDisposable
{
    string Source { get; }

    string Title { get; }

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    Task NavigateAsync(Uri uri);

    void GoBack();

    void GoForward();

    void Reload();

    event EventHandler? SourceChanged;

    event EventHandler? TitleChanged;

    event EventHandler? ProcessFailed;

    event EventHandler? NavigationFailed;

    event EventHandler<TabNavigationResultEventArgs>? NavigationCompleted;

    event EventHandler<BrowserShortcutEventArgs>? ShortcutPressed;

    event EventHandler<ChromeMessageEventArgs>? InternalMessageReceived;

    void PostMessage(string json);
}
