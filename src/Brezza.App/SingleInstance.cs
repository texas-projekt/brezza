using System;
using System.Threading;
using Brezza.App.Native;

namespace Brezza.App;

/// <summary>
/// Keeps one browser window per signed-in user. A second launch brings the existing window to the
/// foreground instead of opening another profile and another WebView2 user-data tree. The mutex is
/// local to the current Windows session, so separate users keep separate browser instances.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Brezza.SingleInstance";
    private Mutex? _mutex;

    /// <summary>
    /// Tries to become the only instance. Returns false when another Brezza process already owns
    /// the name; the caller then activates that window and exits.
    /// </summary>
    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew) return true;
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // A policy or another desktop session can make the name unreadable. Starting the
            // browser is still better than refusing to run; the isolation stays at the process
            // and profile level.
            _mutex?.Dispose();
            _mutex = null;
            return true;
        }
    }

    /// <summary>
    /// Waits briefly for the existing process to create its window, then restores and activates it.
    /// A second launch that races the first one's startup therefore still ends with one window.
    /// </summary>
    public static bool ActivateExisting(TimeSpan? wait = null)
    {
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var window = NativeMethods.FindWindowEx(
                IntPtr.Zero, IntPtr.Zero, MainWindow.WindowClassName, null);
            if (window != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(window, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(window);
                return true;
            }
            Thread.Sleep(100);
        }
        return false;
    }

    public void Dispose()
    {
        if (_mutex is null) return;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was already abandoned or released during shutdown.
        }
        _mutex.Dispose();
        _mutex = null;
    }
}
