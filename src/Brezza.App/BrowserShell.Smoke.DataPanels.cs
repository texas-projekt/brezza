#if DEBUG
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Brezza.App.Native;
using Brezza.Core.Tabs;

// Debug-only smoke helpers for the cookies and browsing-data panels and the cookies they are
// driven against.
namespace Brezza.App;

internal sealed partial class BrowserShell
{

    /// <summary>
    /// The cookies panel lists the profile's cookies and removes them one by one or all at once. The
    /// local test page plants a session cookie and one with an expiry on every load, so the panel
    /// has rows to list; the profile is emptied first, so every count below describes those two and
    /// nothing a page visited earlier in the run may have left behind.
    /// </summary>
    private async Task AssertCookiesPanelAsync()
    {
        var cookiePage = Environment.GetEnvironmentVariable("BREZZA_SMOKE_COOKIE_PAGE") ?? string.Empty;
        if (!Uri.TryCreate(cookiePage, UriKind.Absolute, out var cookieUri))
        {
            throw new InvalidOperationException("The smoke cookie page URL is missing.");
        }

        await OpenPanelAndWaitAsync("cookies");
        if (_runtime?.ActiveHost is null)
        {
            throw new InvalidOperationException("The cookies panel took over the active tab's content host.");
        }

        await ClearAllCookiesThroughPanelAsync();

        await PlantSmokeCookiesAsync(cookieUri);
        await OpenPanelAndWaitAsync("cookies");

        // The panel lists exactly the two cookies the page planted.
        await WaitForAsync(async () => await PanelListCountAsync() == 2, TimeSpan.FromSeconds(10));
        await AssertPanelListCountAsync(2);
        // Both rows show the origin that set them, in the second line the search field reads.
        await AssertPanelFilterLeavesAsync("127.0.0.1", 2);
        // The session cookie is marked as one instead of carrying an expiry date.
        await AssertPanelFilterLeavesAsync(_localization.Get("cookies.session"), 1);
        await AssertPanelFilterLeavesAsync("smokecookie", 1);
        await AssertPanelFilterLeavesAsync("persistcookie", 1);
        await AssertPanelFilterLeavesAsync("no-such-cookie", 0);
        await AssertPanelEmptyStateAsync(visible: true);
        await AssertPanelFilterLeavesAsync(string.Empty, 2);

        // The row's own action removes one cookie without asking, like a bookmark or a history entry.
        await SelectPanelRowAsync(0);
        await PressPanelListKeyAsync(NativeMethods.VK_DELETE);
        await WaitForAsync(async () => await PanelListCountAsync() == 1, TimeSpan.FromSeconds(10));
        await AssertPanelEmptyStateAsync(visible: false);

        // Clearing every cookie asks first and then reports what it did.
        await PlantSmokeCookiesAsync(cookieUri);
        await OpenPanelAndWaitAsync("cookies");
        await WaitForAsync(async () => await PanelListCountAsync() == 2, TimeSpan.FromSeconds(10));
        await AssertPanelListCountAsync(2);
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The cookies panel window was not created.");
        NativeMethods.PostMessage(panel, NativeMethods.WM_COMMAND,
            new IntPtr(InternalPanelWindow.IdClear), NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdClear));
        var dialog = await WaitForOwnedDialogAsync(TimeSpan.FromSeconds(5));
        if (dialog == IntPtr.Zero) throw new InvalidOperationException("Clearing cookies opened no confirmation.");
        await ConfirmDialogAsync(dialog);

        await WaitForNoticeAsync("notice.cookiesCleared");
        _window.InvokeCommand(MainWindow.IdNoticeDismiss);
        await WaitForAsync(() => !_window.IsNoticeVisible, TimeSpan.FromSeconds(5));
        // Nothing is left to list, so the panel falls back to its empty state.
        await WaitForAsync(async () => await PanelListCountAsync() == 0, TimeSpan.FromSeconds(10));
        await AssertPanelEmptyStateAsync(visible: true);
        await ClosePanelAndWaitAsync();
    }

    /// <summary>
    /// Empties the profile's cookies through the panel's own action, so the rows the test page
    /// plants are the only ones the panel lists. A profile that already holds none has no clear
    /// action to run, and its empty list is already the state the next step expects.
    /// </summary>
    private async Task ClearAllCookiesThroughPanelAsync()
    {
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The cookies panel window was not created.");
        var clear = NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdClear);
        if (!NativeMethods.IsWindowEnabled(clear)) return;

        NativeMethods.PostMessage(panel, NativeMethods.WM_COMMAND, new IntPtr(InternalPanelWindow.IdClear), clear);
        var dialog = await WaitForOwnedDialogAsync(TimeSpan.FromSeconds(5));
        if (dialog == IntPtr.Zero) throw new InvalidOperationException("Clearing cookies opened no confirmation.");
        await ConfirmDialogAsync(dialog);
        await WaitForNoticeAsync("notice.cookiesCleared");
        _window.InvokeCommand(MainWindow.IdNoticeDismiss);
        await WaitForAsync(() => !_window.IsNoticeVisible, TimeSpan.FromSeconds(5));
        await WaitForAsync(async () => await PanelListCountAsync() == 0, TimeSpan.FromSeconds(10));
    }

    /// <summary>Loads the page that plants the smoke cookies and waits for it to finish.</summary>
    private async Task PlantSmokeCookiesAsync(Uri cookieUri)
    {
        await NavigateThroughAddressBarAsync(cookieUri.AbsoluteUri);
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { IsLoading: false } host &&
            string.Equals(host.Source, cookieUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// The browsing-data panel lists the four categories of local data the user can clear. A category
    /// is cleared from its own row behind a confirmation, and the footer clears them all the same
    /// way; both report what they did through a notice. The panel is a toolbar view, so the page
    /// keeps its content host.
    /// </summary>
    private async Task AssertBrowsingDataPanelAsync()
    {
        // Ctrl+Shift+Delete is the standard shortcut for clearing browsing data.
        OnShortcutPressed(this, new Brezza.Core.Abstractions.BrowserShortcutEventArgs(0x2E, control: true, alt: false, shift: true));
        await WaitForAsync(() => string.Equals(_panelMode, "data", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("Ctrl+Shift+Delete opened no browsing-data panel.");
        await AssertPanelListCountAsync(4);
        if (_runtime?.ActiveHost is null)
        {
            throw new InvalidOperationException("The browsing-data panel took over the active tab's content host.");
        }

        // The menu command opens the same view over the page.
        await ClosePanelAndWaitAsync();
        _window.InvokeCommand(MainWindow.IdBrowsingData);
        await WaitForAsync(() => string.Equals(_panelMode, "data", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The browsing-data panel window was not created.");

        // One category is cleared from its own row, behind a confirmation.
        await SelectPanelRowAsync(0);
        await PressPanelListKeyAsync(NativeMethods.VK_DELETE);
        var dialog = await WaitForOwnedDialogAsync(TimeSpan.FromSeconds(5));
        if (dialog == IntPtr.Zero) throw new InvalidOperationException("Clearing a data category opened no confirmation.");
        await ConfirmDialogAsync(dialog);
        await WaitForNoticeAsync("notice.dataCleared");
        _window.InvokeCommand(MainWindow.IdNoticeDismiss);
        await WaitForAsync(() => !_window.IsNoticeVisible, TimeSpan.FromSeconds(5));

        // The footer clears every category, behind its own confirmation.
        NativeMethods.PostMessage(panel, NativeMethods.WM_COMMAND,
            new IntPtr(InternalPanelWindow.IdSave), NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdSave));
        dialog = await WaitForOwnedDialogAsync(TimeSpan.FromSeconds(5));
        if (dialog == IntPtr.Zero) throw new InvalidOperationException("Clearing all browsing data opened no confirmation.");
        await ConfirmDialogAsync(dialog);
        await WaitForNoticeAsync("notice.dataCleared");
        _window.InvokeCommand(MainWindow.IdNoticeDismiss);
        await WaitForAsync(() => !_window.IsNoticeVisible, TimeSpan.FromSeconds(5));

        // The categories stay listed: clearing data does not remove the way to clear it again.
        await AssertPanelListCountAsync(4);
        await ClosePanelAndWaitAsync();
    }
}
#endif
