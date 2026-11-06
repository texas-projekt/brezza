#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Brezza.App.Native;
using Brezza.Core.Tabs;

namespace Brezza.App;

/// <summary>
/// Debug-only smoke driver. It walks the main paths through the real native controls: start,
/// navigate, tabs, panels, a private session, one download, and session restore. Details belong in
/// the unit tests; this only proves the pieces are wired together.
/// </summary>
internal sealed partial class BrowserShell
{
    private async Task RunDebugSmokeAsync(string mode)
    {
        var resultPath = Environment.GetEnvironmentVariable("BREZZA_SMOKE_RESULT");
        try
        {
            var page = Environment.GetEnvironmentVariable("BREZZA_SMOKE_PAGE") ?? string.Empty;
            if (!Uri.TryCreate(page, UriKind.Absolute, out var pageUri)) throw new InvalidOperationException("BREZZA_SMOKE_PAGE is missing.");
            await WaitForAsync(() => _runtime is not null);

            if (mode == "restore")
            {
                await WaitForPageAsync(pageUri);
                Check(_downloads?.Downloads.Count > 0, "the download list was not restored");
            }
            else
            {
                await RunMainPathAsync(pageUri);
            }

            WriteSmokeResult(resultPath, "PASS: " + mode);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Smoke failed: {0}", exception);
            WriteSmokeResult(resultPath, "FAIL: " + exception);
        }

        await ShutdownAsync();
    }

    private async Task RunMainPathAsync(Uri pageUri)
    {
        // Start: one empty tab, no content host, the new-tab page listing the seeded history entry.
        Check(_tabs?.Tabs.Count == 1 && _runtime?.ActiveHost is null, "start did not open one empty tab");
        Check(_window.IsNewTabPageVisible, "the new-tab page is not shown");
        Check(_window.StartEntryUrls.Contains("https://example.test/"), "the new-tab page lists no recent pages");

        // Navigate through the address field; the tab, address, and title follow the page.
        await NavigateAsync(pageUri.AbsoluteUri);
        await WaitForPageAsync(pageUri);
        Check(string.Equals(_window.AddressText, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase), "the address field does not show the page");

        // A missing page raises a failure notice that offers Try again.
        var missing = new Uri(new Uri(pageUri, "."), "missing.html");
        await NavigateAsync(missing.AbsoluteUri);
        await WaitForAsync(() => _window.IsNoticeVisible);
        Check(_window.IsControlVisible(MainWindow.IdNoticeAction), "the failure notice offers no Try again");
        _window.InvokeCommand(MainWindow.IdNoticeDismiss);
        await NavigateAsync(pageUri.AbsoluteUri);
        await WaitForPageAsync(pageUri);

        // Tabs: open one, close it with the active-tab button.
        _window.InvokeCommand(MainWindow.IdNewTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 2 && _window.TabCount == 2);
        _window.InvokeCommand(MainWindow.IdCloseActiveTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 1 && _window.TabCount == 1);

        // A duplicate opens right after the tab it copies, and the window title is the tab's label.
        _window.InvokeCommand(MainWindow.IdNewTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 2);
        Check(_window.WindowTitleText.StartsWith(_localization.Get("tab.new"), StringComparison.Ordinal),
            "a new tab's window title is not its label: " + _window.WindowTitleText);
        Check(!_window.IsControlEnabled(MainWindow.IdReload), "reload is enabled on a new-tab page");
        var original = _tabs!.Tabs[0];
        await RunTabCommandAsync(new TabCommand(original.Id, TabCommandKind.Duplicate));
        await WaitForAsync(() => _tabs.Tabs.Count == 3);
        Check(_tabs.Tabs[1].Url == original.Url && string.IsNullOrEmpty(_tabs.Tabs[2].Url),
            "the duplicate did not open next to the tab it copies");
        await RunTabCommandAsync(new TabCommand(_tabs.Tabs[1].Id, TabCommandKind.CloseOthers));
        await WaitForAsync(() => _tabs.Tabs.Count == 1 && _window.TabCount == 1);
        await WaitForPageAsync(pageUri);

        // Panels open over the page and close again; the page keeps its host.
        foreach (var command in new[] { MainWindow.IdSettings, MainWindow.IdHistory, MainWindow.IdBookmarks, MainWindow.IdDownloads })
        {
            _window.InvokeCommand(command);
            await WaitForAsync(() => _panelMode is not null && _panel?.IsOpen == true);
            await ClosePanelAsync();
            Check(_panelMode is null && _runtime?.ActiveHost is not null, "closing a panel lost the page");
        }

        // A private session gets its own profile, which is removed when its last tab closes.
        // The smoke page is a file URL, which history never records, so the private page is an HTTP one.
        var privatePage = Environment.GetEnvironmentVariable("BREZZA_SMOKE_PRIVATE_PAGE") ?? throw new InvalidOperationException("BREZZA_SMOKE_PRIVATE_PAGE is missing.");
        _window.InvokeCommand(MainWindow.IdNewPrivateTab);
        await WaitForAsync(() => _tabs?.ActiveTab?.SessionKind == BrowserSessionKind.Private);
        await NavigateAsync(privatePage);
        await WaitForAsync(() => _runtime?.ActiveHost is { IsLoading: false, Title: "Brezza Private Page" }, TimeSpan.FromSeconds(20));
        var privateProfile = _hostFactory?.PrivateUserDataFolder ?? throw new InvalidOperationException("no private profile was created");
        Check(_historyStore?.GetAll().Any(item => item.Url.StartsWith(privatePage, StringComparison.OrdinalIgnoreCase)) == false,
            "a private page reached history");
        _window.InvokeCommand(MainWindow.IdCloseActiveTab);
        await WaitForAsync(() => !Directory.Exists(privateProfile), TimeSpan.FromSeconds(20));

        // Closing the only tab, a private one, leaves one normal new tab and never an empty strip.
        _window.InvokeCommand(MainWindow.IdCloseActiveTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 1 && string.IsNullOrEmpty(_tabs.ActiveTab?.Url));
        _window.InvokeCommand(MainWindow.IdNewPrivateTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 2);
        _window.InvokeCommand(MainWindow.IdCloseActiveTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 1);
        var onlyPrivate = _tabs!.Tabs[0];
        MiddleClickTab(0);
        await WaitForAsync(() => _tabs.Tabs.Count == 1 && _tabs.ActiveTab?.Id != onlyPrivate.Id);
        Check(_tabs.ActiveTab?.SessionKind == BrowserSessionKind.Normal && _window.TabCount == 1 &&
            _window.IsControlVisible(MainWindow.IdCloseActiveTab), "closing the last tab left no normal tab");
        _window.InvokeCommand(MainWindow.IdNewPrivateTab);
        await WaitForAsync(() => _tabs.Tabs.Count == 2 && _tabs.ActiveTab?.SessionKind == BrowserSessionKind.Private);
        MiddleClickTab(0);
        await WaitForAsync(() => _tabs.Tabs.Count == 1);
        _window.InvokeCommand(MainWindow.IdCloseActiveTab);
        await WaitForAsync(() => _tabs.Tabs.Count == 1 && _tabs.ActiveTab?.SessionKind == BrowserSessionKind.Normal);
        Check(_window.TabCount == 1, "the strip and Core disagree after the last private tab closed");
        await NavigateAsync(pageUri.AbsoluteUri);
        await WaitForPageAsync(pageUri);

        // One real download is written, recorded, and kept for the restore run.
        var download = Environment.GetEnvironmentVariable("BREZZA_SMOKE_DOWNLOAD_PAGE") ?? throw new InvalidOperationException("BREZZA_SMOKE_DOWNLOAD_PAGE is missing.");
        await NavigateAsync(download);
        await WaitForAsync(() => _downloads?.Downloads.Any(item => item.IsTerminal) == true, TimeSpan.FromSeconds(20));
        var file = _downloads!.Downloads.First().TargetFilePath;
        Check(File.Exists(file), "the downloaded file was not written");
        File.Delete(file);

        // Leave the session on the page so the restore run can reopen it.
        await NavigateAsync(pageUri.AbsoluteUri);
        await WaitForPageAsync(pageUri);
    }

    /// <summary>Closes a tab with a middle-click on its strip item, the way a mouse does.</summary>
    private void MiddleClickTab(int index)
    {
        var strip = _window.GetControl(MainWindow.IdTabs);
        var rect = new NativeMethods.RECT();
        NativeMethods.SendMessage(strip, NativeMethods.TCM_GETITEMRECT, new IntPtr(index), ref rect);
        var position = new IntPtr(NativeMethods.MakeLong(rect.left + 6, rect.top + Math.Max(1, rect.Height / 2)));
        NativeMethods.SendMessage(strip, NativeMethods.WM_MBUTTONDOWN, new IntPtr(0x0010), position);
        NativeMethods.SendMessage(strip, NativeMethods.WM_MBUTTONUP, IntPtr.Zero, position);
    }

    private Task WaitForPageAsync(Uri pageUri) => WaitForAsync(() =>
        _runtime?.ActiveHost is { IsLoading: false } host &&
        string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) &&
        host.Title == "Brezza Smoke Page", TimeSpan.FromSeconds(20));

    private static void Check(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for browser state.");
            await Task.Delay(50);
        }
    }

    private static void WriteSmokeResult(string? path, string result)
    {
        if (!string.IsNullOrWhiteSpace(path)) File.WriteAllText(path, result);
    }
}
#endif
