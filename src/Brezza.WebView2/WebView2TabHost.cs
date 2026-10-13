using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Permissions;
using Microsoft.Web.WebView2.Core;
namespace Brezza.WebView2;

/// <summary>
/// A tab page hosted by a WebView2 controller parented to a native window. The host owns the
/// controller lifetime and translates runtime events for <see cref="Core"/>; it never exposes the
/// controller or a script bridge to the page it renders.
/// </summary>
public sealed class WebView2TabHost : ITabHost
{
    private readonly CoreWebView2Environment _environment;
    private readonly IntPtr _parentWindow;
    private readonly CoreWebView2ControllerOptions _controllerOptions;
    private readonly string _downloadsFolder;
    private readonly TaskCompletionSource<CoreWebView2Controller> _controllerCreation =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private TaskCompletionSource<bool>? _pendingNavigation;
    private CoreWebView2Controller? _controller;
    private Task<CoreWebView2Controller>? _creationTask;
    private bool _disposed;
    private bool _isLoading;
    private readonly string _hostId = "host-" + Guid.NewGuid().ToString("N").Substring(0, 8);

    // The shell positions and shows a host as soon as it becomes active, which is usually before the
    // controller exists. The requested view state is kept here and applied when the controller is ready.
    private bool _visible;
    private System.Drawing.Rectangle? _bounds;
    private double? _rasterizationScale;
    private bool _focusPending;


    internal WebView2TabHost(
        CoreWebView2Environment environment,
        IntPtr parentWindow,
        CoreWebView2ControllerOptions controllerOptions,
        string? downloadsFolder = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _parentWindow = parentWindow;
        _controllerOptions = controllerOptions;
        _downloadsFolder = downloadsFolder ?? string.Empty;
    }

    public string Source => _controller?.CoreWebView2?.Source ?? string.Empty;

    public string Title => _controller?.CoreWebView2?.DocumentTitle ?? string.Empty;

    public bool CanGoBack => _controller?.CoreWebView2?.CanGoBack ?? false;

    public bool CanGoForward => _controller?.CoreWebView2?.CanGoForward ?? false;

    public bool IsLoading => _isLoading && !_disposed;

    public bool IsDocumentPlayingAudio => _controller?.CoreWebView2?.IsDocumentPlayingAudio ?? false;

    public bool ContainsFullScreenElement => _controller?.CoreWebView2?.ContainsFullScreenElement ?? false;

    public event EventHandler? SourceChanged;

    public event EventHandler? TitleChanged;

    public event EventHandler? ProcessFailed;

    public event EventHandler? NavigationFailed;

    public event EventHandler<TabNavigationResultEventArgs>? NavigationCompleted;

    /// <summary>A page asked the browser to open a new window.</summary>
    public event EventHandler<PopupRequestedEventArgs>? PopupRequested;

    public event EventHandler<DownloadStartedEventArgs>? DownloadStarted;

    public event EventHandler<DownloadStatusChangedEventArgs>? DownloadStatusChanged;

    public event EventHandler<PermissionRequestedEventArgs>? PermissionRequested;

    internal static WebView2TabHost Create(
        CoreWebView2Environment environment,
        IntPtr parentWindow,
        CoreWebView2ControllerOptions controllerOptions,
        string? downloadsFolder = null)
    {
        var host = new WebView2TabHost(environment, parentWindow, controllerOptions, downloadsFolder);
        host.StartCreation();
        return host;
    }

    /// <summary>Shows or hides the surface. Only the active tab's host is visible.</summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_disposed || _controller is not { } controller) return;
        controller.IsVisible = visible;
    }

    /// <summary>Gives keyboard focus to the rendered page.</summary>
    public void Focus()
    {
        if (_disposed) return;
        if (_controller is null)
        {
            _focusPending = true;
            return;
        }

        try
        {
            _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is System.Runtime.InteropServices.COMException)
        {
            // The controller can be closing while focus moves; focus is not a correctness signal.
        }
    }

    /// <summary>Positions the surface inside its parent window, in parent client coordinates.</summary>
    public void SetBounds(int x, int y, int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0) return;
        _bounds = new System.Drawing.Rectangle(x, y, width, height);
        if (_controller is not { } controller) return;
        try
        {
            controller.Bounds = _bounds.Value;
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is System.Runtime.InteropServices.COMException)
        {
            Trace.TraceWarning("The tab host surface could not be resized: {0}", exception.Message);
        }
    }

    /// <summary>Sets the rasterization scale that matches the host window's DPI.</summary>
    public void SetRasterizationScale(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) return;
        _rasterizationScale = scale;
        if (_disposed || _controller is not { } controller) return;
        try
        {
            controller.RasterizationScale = scale;
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is System.Runtime.InteropServices.COMException)
        {
            Trace.TraceWarning("The tab host rasterization scale could not be applied: {0}", exception.Message);
        }
    }

    public async Task NavigateAsync(Uri uri)
    {
        ThrowIfDisposed();
        var controller = await _controllerCreation.Task.ConfigureAwait(true);
        ThrowIfDisposed();
        await _navigationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
            handler = (_, args) => completion.TrySetResult(args.IsSuccess);
            _pendingNavigation = completion;
            controller.CoreWebView2.NavigationCompleted += handler;
            try
            {
                controller.CoreWebView2.Navigate(uri.AbsoluteUri);
                var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(true);
                if (!ReferenceEquals(completed, completion.Task))
                {
                    throw new TimeoutException("WebView2 navigation did not complete within 30 seconds.");
                }

                await completion.Task.ConfigureAwait(true);
            }
            finally
            {
                controller.CoreWebView2.NavigationCompleted -= handler;
                if (ReferenceEquals(_pendingNavigation, completion)) _pendingNavigation = null;
            }
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    public void GoBack()
    {
        ThrowIfDisposed();
        if (_controller is { CoreWebView2.CanGoBack: true } controller) controller.CoreWebView2.GoBack();
    }

    public void GoForward()
    {
        ThrowIfDisposed();
        if (_controller is { CoreWebView2.CanGoForward: true } controller) controller.CoreWebView2.GoForward();
    }

    public void Reload()
    {
        ThrowIfDisposed();
        _controller?.CoreWebView2.Reload();
    }

    /// <summary>
    /// Runs a script in the rendered document on behalf of the shell. This is a host-side
    /// verification tool; remote pages receive no equivalent capability.
    /// </summary>
    public Task<string> ExecuteScriptAsync(string script)
    {
        ThrowIfDisposed();
        if (_controller is null) throw new InvalidOperationException("WebView2 is not initialized.");
        return _controller.CoreWebView2.ExecuteScriptAsync(script);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isLoading = false;
        _pendingNavigation?.TrySetException(new ObjectDisposedException(nameof(WebView2TabHost)));
        _pendingNavigation = null;

        if (_controller is { } controller)
        {
            _controller = null;
            Detach(controller);
            controller.IsVisible = false;
            controller.Close();
            // A disposed host must never hand out a closed controller.
            _controllerCreation.TrySetCanceled();
            return;
        }

        // Creation is still in flight: the controller is closed as soon as it exists, so a tab that
        // closes during startup cannot leak a live WebView2 surface.
        _controllerCreation.TrySetCanceled();
        ObserveLateCreation();
    }

    private void StartCreation()
    {
        if (_disposed)
        {
            _controllerCreation.TrySetCanceled();
            return;
        }

        _creationTask = CreateControllerAsync();
    }

    private async Task<CoreWebView2Controller> CreateControllerAsync()
    {
        System.Diagnostics.Trace.TraceInformation("Tab host {0}: creating controller.", _hostId);
        try
        {
            var controller = await _environment.CreateCoreWebView2ControllerAsync(_parentWindow, _controllerOptions)
                .ConfigureAwait(true);
            System.Diagnostics.Trace.TraceInformation("Tab host {0}: controller created.", _hostId);
            await ConfigureControllerAsync(controller).ConfigureAwait(true);
            return controller;
        }
        catch (Exception exception)
        {
            // A creation failure must reach the caller: a tab that cannot render reports it instead
            // of waiting for a controller that will never exist.
            System.Diagnostics.Trace.TraceError("Tab host {0}: controller creation failed: {1}", _hostId, exception);
            _controllerCreation.TrySetException(exception);
            throw;
        }
    }

    private async Task ConfigureControllerAsync(CoreWebView2Controller controller)
    {
        Attach(controller);
        var core = controller.CoreWebView2;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        // Browser accelerators (F5, Ctrl+R, ...) are handled by the native window, not the page.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        controller.IsVisible = false;

        if (_disposed)
        {
            // The tab was closed while the controller was being created.
            Detach(controller);
            controller.Close();
            _controllerCreation.TrySetCanceled();
            return;
        }

        _controller = controller;
        ApplyRequestedViewState(controller);
        _controllerCreation.TrySetResult(controller);
    }

    /// <summary>
    /// Applies the bounds, scale, visibility, and focus the shell asked for while the controller was
    /// still being created. Without this a tab restored at startup navigates in a hidden, zero-size
    /// surface: the address bar shows its URL but the page never appears.
    /// </summary>
    private void ApplyRequestedViewState(CoreWebView2Controller controller)
    {
        try
        {
            if (_rasterizationScale is { } scale) controller.RasterizationScale = scale;
            if (_bounds is { } bounds) controller.Bounds = bounds;
            controller.IsVisible = _visible;
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is System.Runtime.InteropServices.COMException)
        {
            Trace.TraceWarning("Tab host {0}: the requested view state could not be applied: {1}", _hostId, exception.Message);
        }

        if (_focusPending && _visible)
        {
            _focusPending = false;
            Focus();
        }
    }

    /// <summary>Closes a controller that was created after the host was disposed.</summary>
    private void ObserveLateCreation()
    {
        var creation = _creationTask;
        if (creation is null) return;
        _ = CloseLateCreationAsync(creation);
    }

    private static async Task CloseLateCreationAsync(Task<CoreWebView2Controller> creation)
    {
        try
        {
            var controller = await creation.ConfigureAwait(true);
            controller.Close();
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException ||
            exception is TaskCanceledException ||
            exception is InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceInformation(
                "A tab host closed before its WebView2 controller was ready: {0}", exception.Message);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "A late WebView2 controller could not be closed: {0}", exception);
        }
    }

    private void Attach(CoreWebView2Controller controller)
    {
        var core = controller.CoreWebView2;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameCreated += OnFrameCreated;
        core.SourceChanged += OnSourceChanged;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.ProcessFailed += OnProcessFailed;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
    }

    private void Detach(CoreWebView2Controller controller)
    {
        var core = controller.CoreWebView2;
        core.NavigationStarting -= OnNavigationStarting;
        core.FrameCreated -= OnFrameCreated;
        core.SourceChanged -= OnSourceChanged;
        core.DocumentTitleChanged -= OnDocumentTitleChanged;
        core.ProcessFailed -= OnProcessFailed;
        core.NavigationCompleted -= OnNavigationCompleted;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.PermissionRequested -= OnPermissionRequested;
        core.DownloadStarting -= OnDownloadStarting;
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args) =>
        SourceChanged?.Invoke(this, EventArgs.Empty);

    private void OnDocumentTitleChanged(object? sender, object args) =>
        TitleChanged?.Invoke(this, EventArgs.Empty);

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        _isLoading = true;
    }

    private void OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs args)
    {
        // Frames are tracked for observability only; no browser-owned document exists in a content
        // surface, so a frame can never reach trusted browser UI.
        _ = args;
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        _isLoading = false;
        _pendingNavigation?.TrySetException(new InvalidOperationException("The WebView2 process failed during navigation."));
        ProcessFailed?.Invoke(this, EventArgs.Empty);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        _isLoading = false;
        NavigationCompleted?.Invoke(this, new TabNavigationResultEventArgs(args.IsSuccess, args.WebErrorStatus.ToString()));
        if (!args.IsSuccess) NavigationFailed?.Invoke(this, EventArgs.Empty);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // A popup becomes an ordinary new tab; the shell never opens a second window on its own.
        args.Handled = true;
        PopupRequested?.Invoke(this, new PopupRequestedEventArgs(args.Uri));
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        // Core owns the decision; this surface only reports the request and the answer.
        var kind = MapPermissionKind(args.PermissionKind);
        var decision = BrowserPermissionPolicy.Evaluate(kind);
        args.Handled = true;
        args.State = decision.IsAllowed
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;
        PermissionRequested?.Invoke(this, new PermissionRequestedEventArgs(kind, args.Uri, decision.IsAllowed));
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        var operation = args.DownloadOperation;
        var suggested = BrowserDownloadPaths.SuggestedFileNameFrom(operation.ContentDisposition, operation.Uri);
        var target = BrowserDownloadPaths.ResolveTargetPath(_downloadsFolder, suggested, File.Exists);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        operation.StateChanged += OnDownloadStateChanged;
        args.ResultFilePath = target;
        // Handling the event keeps the runtime's own download dialog out of the shell.
        args.Handled = true;
        DownloadStarted?.Invoke(this, new DownloadStartedEventArgs(operation.Uri, suggested, operation.MimeType, target));
    }

    private void OnDownloadStateChanged(object? sender, object e)
    {
        if (sender is not CoreWebView2DownloadOperation operation) return;
        var state = operation.State switch
        {
            CoreWebView2DownloadState.InProgress => BrowserDownloadState.InProgress,
            CoreWebView2DownloadState.Completed => BrowserDownloadState.Completed,
            _ => BrowserDownloadState.Interrupted,
        };
        if (state == BrowserDownloadState.InProgress) return;
        operation.StateChanged -= OnDownloadStateChanged;
        DownloadStatusChanged?.Invoke(this, new DownloadStatusChangedEventArgs(
            state,
            operation.ResultFilePath,
            operation.Uri,
            operation.TotalBytesToReceive,
            state == BrowserDownloadState.Interrupted ? operation.InterruptReason.ToString() : null));
    }

    private static BrowserPermissionKind MapPermissionKind(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => BrowserPermissionKind.MediaCapture,
        CoreWebView2PermissionKind.Camera => BrowserPermissionKind.MediaCapture,
        CoreWebView2PermissionKind.Geolocation => BrowserPermissionKind.Geolocation,
        CoreWebView2PermissionKind.Notifications => BrowserPermissionKind.Notifications,
        CoreWebView2PermissionKind.ClipboardRead => BrowserPermissionKind.Clipboard,
        CoreWebView2PermissionKind.FileReadWrite => BrowserPermissionKind.FileSystem,
        _ => BrowserPermissionKind.Other,
    };

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WebView2TabHost));
    }
}
