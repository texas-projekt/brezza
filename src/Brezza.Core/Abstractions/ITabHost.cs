using System.IO;
using System.Threading.Tasks;
using Brezza.Core.Tabs;

namespace Brezza.Core.Abstractions;

public interface ITabHost : IDisposable
{
    string Source { get; }

    string Title { get; }

    /// <summary>
    /// The favicon image URI of the current document, or empty when the document has none. The
    /// image itself is fetched with <see cref="GetFaviconAsync"/>, never by the shell.
    /// </summary>
    string FaviconUri { get; }

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    /// <summary>True while a navigation is in flight. Unknown means false only for a disposed host.</summary>
    bool IsLoading { get; }

    /// <summary>True when the document plays audio.</summary>
    bool IsDocumentPlayingAudio { get; }

    /// <summary>True when the document shows a full-screen element.</summary>
    bool ContainsFullScreenElement { get; }

    /// <summary>
    /// Shows or hides the host surface inside its parent window. Only the active tab's host is
    /// visible; the adapter decides how that is expressed.
    /// </summary>
    void SetVisible(bool visible);

    /// <summary>
    /// Positions the host surface inside its parent window, in parent client coordinates.
    /// </summary>
    void SetBounds(int x, int y, int width, int height);

    /// <summary>Sets the rendering scale that matches the host window's DPI.</summary>
    void SetRasterizationScale(double scale);

    /// <summary>Gives keyboard focus to the rendered page.</summary>
    void Focus();

    /// <summary>
    /// Sets the background the surface shows before its page paints, as a Win32 COLORREF. Rendered
    /// pages keep controlling their own pixels; this only covers the unpainted surface.
    /// </summary>
    void SetBackgroundColor(uint color);

    /// <summary>Tells pages whether the browser chrome is dark, through the CSS prefers-color-scheme media feature. Pages that define their own colors keep them.</summary>
    void SetPrefersDarkColorScheme(bool dark);

    /// <summary>
    /// The current document's favicon image as an encoded stream, or null when the document has
    /// none or no host exists. The host fetches it, so the request stays inside the page's own
    /// profile and network stack; the caller owns the returned stream.
    /// </summary>
    Task<Stream?> GetFaviconAsync();

    Task NavigateAsync(Uri uri);

    void GoBack();

    void GoForward();

    void Reload();

    event EventHandler? SourceChanged;

    event EventHandler? TitleChanged;

    /// <summary>The current document's favicon URI changed, including to empty when it has none.</summary>
    event EventHandler? FaviconChanged;

    event EventHandler? ProcessFailed;

    event EventHandler? NavigationFailed;

    event EventHandler<TabNavigationResultEventArgs>? NavigationCompleted;

    /// <summary>A page asked the browser to open a new window.</summary>
    event EventHandler<PopupRequestedEventArgs>? PopupRequested;

    /// <summary>A download started and was given a local target file.</summary>
    event EventHandler<DownloadStartedEventArgs>? DownloadStarted;

    /// <summary>A download changed state.</summary>
    event EventHandler<DownloadStatusChangedEventArgs>? DownloadStatusChanged;

    /// <summary>A page asked for a capability and the browser answered it.</summary>
    event EventHandler<PermissionRequestedEventArgs>? PermissionRequested;
}
