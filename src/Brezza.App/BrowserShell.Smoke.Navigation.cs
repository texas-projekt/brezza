#if DEBUG
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Brezza.App.Native;
using Brezza.Core.Tabs;

// Debug-only smoke helpers for navigation: the address field, page scripts, the trust probe,
// and the failure and notice paths.
namespace Brezza.App;

internal sealed partial class BrowserShell
{

    private async Task NavigateThroughAddressBarAsync(string value)
    {
        var combo = _window.GetControl(MainWindow.IdAddress);
        NativeMethods.SetWindowText(combo, value);
        var edit = NativeMethods.FindWindowEx(combo, IntPtr.Zero, "EDIT", null);
        if (edit == IntPtr.Zero)
        {
            throw new InvalidOperationException("The native address field has no edit control.");
        }

        NativeMethods.SendMessage(edit, NativeMethods.WM_KEYDOWN, new IntPtr(NativeMethods.VK_RETURN), IntPtr.Zero);
        await Task.Yield();
    }

    private async Task<string> ExecuteActivePageScriptAsync(string script)
    {
        if (_runtime?.ActiveHost is not Brezza.WebView2.WebView2TabHost host)
        {
            throw new InvalidOperationException("The active tab is not a WebView2 content page.");
        }

        return await host.ExecuteScriptAsync(script);
    }

    /// <summary>
    /// Reads the trust probe's report. The probe navigates away on a timer, so a failed script run
    /// is retried and the last observed report is returned for the assertion message.
    /// </summary>
    private async Task<string> WaitForProbeReportAsync(bool settled)
    {
        var report = string.Empty;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(settled ? 40 : 15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                report = await ExecuteActivePageScriptAsync("JSON.stringify(window.trustProbe||{})");
                var ready = report.IndexOf("bridge", StringComparison.Ordinal) >= 0;
                if (settled)
                {
                    ready = report.IndexOf("rejected", StringComparison.Ordinal) >= 0 ||
                            report.IndexOf("status:", StringComparison.Ordinal) >= 0;
                }

                if (ready) return report;
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
            {
                // The document is navigating; the next poll reads the new one.
            }

            await Task.Delay(50);
        }

        return report;
    }

    /// <summary>
    /// Reloads rapidly through the F5 shortcut path. A reload that cancels the previous one must not
    /// raise a failure notice, and an unchanged page must not re-lay out and erase the whole window:
    /// either one shows as a flashing band below the toolbar.
    /// </summary>
    private async Task AssertRapidReloadIsStableAsync()
    {
        var rendersBefore = _window.StartPageRenders;
        for (var round = 0; round < 5; round++)
        {
            OnShortcutPressed(this, new Brezza.Core.Abstractions.BrowserShortcutEventArgs(0x74, false, false, false));
            await Task.Delay(60);
            if (_window.IsNoticeVisible)
                throw new InvalidOperationException("A rapid reload raised a notice: " + _window.NoticeText);
        }

        await WaitForAsync(() => _runtime?.ActiveHost is { IsLoading: false }, TimeSpan.FromSeconds(20));
        if (_window.IsNoticeVisible)
            throw new InvalidOperationException("A rapid reload left a notice: " + _window.NoticeText);
        var renders = _window.StartPageRenders - rendersBefore;
        if (renders != 0)
            throw new InvalidOperationException("Reloading an unchanged page re-laid out the whole window " + renders + " time(s).");
    }

    /// <summary>
    /// The failure notice offers Reload. Reload must retry the address that failed, not the page
    /// before it, and once that address loads, the notice goes away and the tab is no longer failed.
    /// </summary>
    private async Task AssertNoticeReloadRecoversAsync(Uri missingUri)
    {
        if (!_window.IsControlVisible(MainWindow.IdNoticeAction))
            throw new InvalidOperationException("The failure notice offers no Reload action.");

        // While the notice is shown, the page appears; Reload through the notice button must load it.
        File.WriteAllText(missingUri.LocalPath, "<!doctype html><title>Recovered Page</title><h1>Recovered</h1>");
        try
        {
            ClickButton(MainWindow.IdNoticeAction);
            await WaitForAsync(() =>
                _runtime?.ActiveHost is { IsLoading: false } host &&
                string.Equals(host.Title, "Recovered Page", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
            if (!string.Equals(_runtime!.ActiveHost!.Source, missingUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Reload did not retry the failed address: " + _runtime.ActiveHost.Source);
            await WaitForAsync(() => !_window.IsNoticeVisible, TimeSpan.FromSeconds(5));
            if (_tabs?.ActiveTab?.State == TabRuntimeState.Failed)
                throw new InvalidOperationException("The tab stayed failed after its page loaded.");
            if (_window.WindowTitle.IndexOf(_localization.Get("notice.navigationFailed"), StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("The window title still reports the failure: " + _window.WindowTitle);
        }
        finally
        {
            File.Delete(missingUri.LocalPath);
        }
    }

}
#endif
