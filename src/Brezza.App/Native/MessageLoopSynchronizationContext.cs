using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Brezza.App.Native;

/// <summary>
/// Marshals <see cref="SynchronizationContext"/> work to the thread that owns the native message
/// loop. Every <c>await</c> continuation in the shell runs here, so WebView2 controllers and window
/// handles are only ever touched on the thread that created them.
/// </summary>
internal sealed class MessageLoopSynchronizationContext : SynchronizationContext, IDisposable
{
    private const uint DrainQueueMessage = 0x8001; // WM_APP + 1
    private static readonly IntPtr MessageOnlyWindowParent = new(-3); // HWND_MESSAGE
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly NativeMethods.WndProc _wndProc;
    private readonly IntPtr _hwnd;
    private readonly Queue<WorkItem> _queue = new();
    private readonly object _gate = new();
    private readonly int _uiThreadId;
    private bool _disposed;

    public MessageLoopSynchronizationContext()
    {
        _uiThreadId = Thread.CurrentThread.ManagedThreadId;
        _wndProc = OnWindowMessage;

        var instance = NativeMethods.GetModuleHandle(null);
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            lpszClassName = "BrezzaSynchronizationContext",
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            "BrezzaSynchronizationContext",
            string.Empty,
            0,
            0, 0, 0, 0,
            MessageOnlyWindowParent,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The browser message-loop window could not be created (error " +
                System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ").");
        }
    }

    public IntPtr Handle => _hwnd;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (callback is null) throw new ArgumentNullException(nameof(callback));
        lock (_gate)
        {
            if (_disposed)
            {
                Trace.TraceWarning("A continuation was dropped because the message loop had ended.");
                return;
            }

            _queue.Enqueue(new WorkItem(callback, state));
        }

        if (!NativeMethods.PostMessage(_hwnd, DrainQueueMessage, IntPtr.Zero, IntPtr.Zero))
        {
            Trace.TraceWarning("The message-loop notification could not be posted.");
        }
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (callback is null) throw new ArgumentNullException(nameof(callback));
        if (Thread.CurrentThread.ManagedThreadId == _uiThreadId)
        {
            callback(state);
            return;
        }

        var completion = new ManualResetEventSlim(false);
        lock (_gate)
        {
            if (_disposed)
            {
                Trace.TraceWarning("A send was dropped because the message loop had ended.");
                return;
            }

            _queue.Enqueue(new WorkItem(callback, state, completion));
        }

        NativeMethods.PostMessage(_hwnd, DrainQueueMessage, IntPtr.Zero, IntPtr.Zero);
        if (!completion.Wait(SendTimeout))
        {
            Trace.TraceError("A synchronous continuation did not run on the message loop within {0}.", SendTimeout);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(_hwnd);
    }

    private IntPtr OnWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == DrainQueueMessage)
            {
                Drain();
                return IntPtr.Zero;
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("A message-loop queue message failed: {0}", exception);
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Drain()
    {
        while (true)
        {
            WorkItem item;
            lock (_gate)
            {
                if (_queue.Count == 0) return;
                item = _queue.Dequeue();
            }

            try
            {
                item.Callback(item.State);
            }
            catch (Exception exception)
            {
                // A failed continuation must not stop the message loop or the browser.
                Trace.TraceError("A message-loop continuation failed: {0}", exception);
            }
            finally
            {
                item.Completion?.Set();
            }
        }
    }

    private readonly struct WorkItem
    {
        public WorkItem(SendOrPostCallback callback, object? state, ManualResetEventSlim? completion = null)
        {
            Callback = callback;
            State = state;
            Completion = completion;
        }

        public SendOrPostCallback Callback { get; }

        public object? State { get; }

        public ManualResetEventSlim? Completion { get; }
    }
}
