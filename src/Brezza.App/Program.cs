using System;
using Brezza.App.Native;

namespace Brezza.App;

internal static class Program
{
    /// <summary>
    /// Starts the native shell on a single STA thread with a message loop that also carries
    /// <see cref="System.Threading.SynchronizationContext"/> continuations.
    /// </summary>
    [STAThread]
    private static int Main()
    {
        MessageLoopSynchronizationContext? context = null;
        System.Diagnostics.TraceListener? log = null;
        try
        {
            // An optional trace file makes native lifetime problems observable during diagnosis.
            var tracePath = Environment.GetEnvironmentVariable("BREZZA_TRACE_LOG");
            if (!string.IsNullOrWhiteSpace(tracePath))
            {
                log = new System.Diagnostics.TextWriterTraceListener(tracePath);
                System.Diagnostics.Trace.Listeners.Add(log);
                System.Diagnostics.Trace.AutoFlush = true;
            }

            context = new MessageLoopSynchronizationContext();
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            using var shell = new BrowserShell();
            return shell.Run();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Brezza could not start: {0}", exception);
            NativeMethods.MessageBox(
                IntPtr.Zero,
                exception.Message,
                "Brezza",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
            return 1;
        }
        finally
        {
            System.Diagnostics.Trace.Flush();
            log?.Dispose();
            context?.Dispose();
        }
    }
}
