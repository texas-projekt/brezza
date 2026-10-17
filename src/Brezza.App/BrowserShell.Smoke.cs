#if DEBUG
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Brezza.App.Native;
using Brezza.Core.Tabs;

namespace Brezza.App;

/// <summary>
/// Debug-only integration driver. It drives the real native controls by sending the window
/// messages a user's input produces, so the smoke test covers the shipped Win32 UI rather than a
/// removed HTML bridge. The helpers are split into partial files by concern: navigation, native
/// input, the native panels, and the data panels.
/// </summary>
internal sealed partial class BrowserShell
{
    private async Task RunDebugSmokeAsync(string mode)
    {
        var resultPath = Environment.GetEnvironmentVariable("BREZZA_SMOKE_RESULT");
        try
        {
            var pageUrl = Environment.GetEnvironmentVariable("BREZZA_SMOKE_PAGE") ?? string.Empty;
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
            {
                throw new InvalidOperationException("Smoke page URL is missing.");
            }

            if (mode == "restore")
            {
                await WaitForAsync(() =>
                    _runtime?.ActiveHost is { } host &&
                    string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(host.Title, "Brezza Smoke Page", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
                WriteSmokeResult(resultPath, "PASS: session restored through the native shell.");
                await ShutdownAsync();
                return;
            }

            if (mode == "startup")
            {
                // The default startup behavior opens the new-tab page even though a session is
                // saved; restoring it is a setting the user turns on.
                await WaitForAsync(() => _runtime is not null, TimeSpan.FromSeconds(20));
                if (_runtime?.ActiveHost is not null)
                {
                    throw new InvalidOperationException("A new tab created a content host before navigation.");
                }

                if (!_window.IsNewTabPageVisible)
                {
                    throw new InvalidOperationException("The default startup did not open the new-tab page.");
                }

                if (_tabs?.Tabs.Count != 1 || !string.IsNullOrEmpty(_tabs.ActiveTab?.Url))
                {
                    throw new InvalidOperationException(
                        "The default startup did not start from one empty tab: tabs=" + _tabs?.Tabs.Count +
                        " url=[" + _tabs?.ActiveTab?.Url + "]");
                }

                if (!string.IsNullOrEmpty(_window.AddressText))
                {
                    throw new InvalidOperationException("The default startup restored an address: " + _window.AddressText);
                }

                WriteSmokeResult(resultPath, "PASS: startup opens the new-tab page by default.");
                await ShutdownAsync();
                return;
            }

            await RunInitialSmokeAsync(pageUri);
            WriteSmokeResult(resultPath, "PASS: native controls, tabs, favicons, private cleanup, failure state, toolbar panels, cookie and browsing-data clearing, and remote trust boundary.");
            await ShutdownAsync();
        }
        catch (Exception exception)
        {
            WriteSmokeResult(resultPath, "FAIL: " + exception + Environment.NewLine + DescribeNativeState());
            await ShutdownAsync();
        }
    }

    /// <summary>A snapshot of the native controls and browser state, used only for diagnostics.</summary>
    private string DescribeNativeState()
    {
        try
        {
            var tabs = string.Join(" | ", Enumerable.Range(0, _window.TabCount).Select(index => _window.GetTabText(index)));
            return "state: tabs=" + _window.TabCount + " selected=" + _window.SelectedTabIndex +
                   " tabTexts=[" + tabs + "] address=[" + _window.AddressText + "]" +
                   " notice=" + _window.IsNoticeVisible + " [" + _window.NoticeText + "]" +
                   " newTabPage=" + _window.IsNewTabPageVisible +
                   " panel=" + (_panelMode ?? "(none)") +
                   " activeTab=" + (_tabs?.ActiveTab?.Id ?? "(none)") + " [" + (_tabs?.ActiveTab?.Url ?? string.Empty) + "]" +
                   " favicon=[" + (_tabs?.ActiveTab?.FaviconUri ?? string.Empty) + "]" +
                   " host=" + (_runtime?.ActiveHost?.Source ?? "(none)") +
                   " title=[" + _runtime?.ActiveHost?.Title + "]";
        }
        catch (Exception exception)
        {
            return "state unavailable: " + exception.Message;
        }
    }

    private async Task RunInitialSmokeAsync(Uri pageUri)
    {
        // 1. A new tab is the browser-owned new-tab page and owns no content host.
        await WaitForAsync(() => _runtime is not null, TimeSpan.FromSeconds(20));
        if (_runtime?.ActiveHost is not null)
        {
            throw new InvalidOperationException("A new tab created a content host before navigation.");
        }

        if (!_window.IsNewTabPageVisible)
        {
            throw new InvalidOperationException("The native new-tab page is not visible.");
        }

        if (_window.TabCount != 1)
        {
            throw new InvalidOperationException("The native tab strip does not hold exactly one tab: " + _window.TabCount);
        }

        var newTabText = _window.GetTabText(0);
        if (newTabText.IndexOf("标签页", StringComparison.Ordinal) < 0 && newTabText.IndexOf("New Tab", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("The native tab strip shows an unexpected title: " + newTabText);
        }

        // 2. Settings, history, and bookmarks are native panels driven by the native buttons.
        await OpenPanelAndWaitAsync("settings");
        await AssertPanelSettingsAsync();
        await PauseForPanelInspectionAsync();
        await ClosePanelAndWaitAsync();

        // Opening the address combo box must populate the real suggestions list.
        var addressCombo = _window.GetControl(MainWindow.IdAddress);
        NativeMethods.SendMessage(addressCombo, NativeMethods.CB_SHOWDROPDOWN, new IntPtr(1), IntPtr.Zero);
        var suggestionCount = (int)NativeMethods.SendMessage(
            addressCombo, NativeMethods.CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (suggestionCount < 1)
        {
            throw new InvalidOperationException("The address drop button did not populate suggestions.");
        }
        NativeMethods.SendMessage(addressCombo, NativeMethods.CB_SHOWDROPDOWN, IntPtr.Zero, IntPtr.Zero);

        await OpenPanelAndWaitAsync("history");
        await AssertPanelListCountAsync(1);
        await AssertPanelEmptyStateAsync(visible: false);
        await AssertPanelTitleAsync("history");
        await PauseForHistoryInspectionAsync();
        await ClosePanelAndWaitAsync();

        // Closing a panel after a modal confirmation is where Windows stops returning focus to
        // the browser, so the clear-then-close path is driven through the panel's own actions.
        await AssertPanelCloseReturnsFocusAsync();

        await OpenPanelAndWaitAsync("bookmarks");
        await AssertPanelListCountAsync(0);
        await AssertPanelEmptyStateAsync(visible: true);
        await PauseForBookmarksInspectionAsync();
        await ClosePanelAndWaitAsync();

        // 3. Address field navigation into a local page. The wait covers the whole navigation:
        // the document title arrives before NavigationCompleted, so the loading marker stays on
        // the active tab until the host reports that loading finished.
        await NavigateThroughAddressBarAsync(pageUri.AbsoluteUri);
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { } host &&
            !host.IsLoading &&
            string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(host.Title, "Brezza Smoke Page", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
        AssertActiveTabFinishedLoading();

        // The content surface follows the chrome palette: an unpainted page must not show the
        // WebView2 default white against the themed chrome.
        var contentHost = _runtime?.ActiveHost as Brezza.WebView2.WebView2TabHost
            ?? throw new InvalidOperationException("The active tab is not a WebView2 content page.");
        var expectedBackground = _window.ContentBackgroundColor;
        if (contentHost.BackgroundColor != expectedBackground)
        {
            throw new InvalidOperationException(
                "The content surface background does not follow the chrome palette: 0x" +
                contentHost.BackgroundColor.ToString("X6") + " != 0x" + expectedBackground.ToString("X6"));
        }

        // Pages read the chrome's light/dark scheme through prefers-color-scheme.
        if (contentHost.PrefersDarkColorScheme != _theme.Palette.IsDark)
        {
            throw new InvalidOperationException(
                "The content color scheme does not follow the chrome: prefers dark = " +
                (contentHost.PrefersDarkColorScheme?.ToString() ?? "auto") + ", chrome dark = " + _theme.Palette.IsDark);
        }

        var pageScheme = await ExecuteActivePageScriptAsync("matchMedia('(prefers-color-scheme: dark)').matches");
        if (!string.Equals(pageScheme, _theme.Palette.IsDark ? "true" : "false", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The page does not see the chrome color scheme: prefers-color-scheme dark = " + pageScheme);
        }

        if (!string.Equals(_window.AddressText, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The native address field does not show the current page: " + _window.AddressText);
        }

        // The tab shows the page's favicon: Core tracked the URI from the host's event, and the
        // shell decoded the rasterized image the host fetched, ready for the strip to paint. The
        // page declares an emoji in an inline SVG data URI, so this also proves the runtime
        // rasterizes an SVG favicon into the bitmap the strip paints.
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { FaviconUri.Length: > 0 } host &&
            host.FaviconUri.StartsWith("data:image/svg+xml,", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        await WaitForAsync(() =>
            _tabs?.ActiveTab is { FaviconUri.Length: > 0 } tab &&
            _favicons?.Image(tab.Id) is not null,
            TimeSpan.FromSeconds(10));

        await AssertRapidReloadIsStableAsync();

        // Escape must restore the committed address through the inner EDIT control.
        _window.FocusAddress();
        var escapeCombo = _window.GetControl(MainWindow.IdAddress);
        NativeMethods.SetWindowText(escapeCombo, "temporary text");
        var addressEdit = NativeMethods.FindWindowEx(escapeCombo, IntPtr.Zero, "EDIT", null);
        NativeMethods.SendMessage(addressEdit, NativeMethods.WM_KEYDOWN,
            new IntPtr(NativeMethods.VK_ESCAPE), IntPtr.Zero);
        if (!string.Equals(_window.AddressText, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Escape did not restore the committed address: " + _window.AddressText);
        }

        // 4. Browser-owned pages are toolbar views, not routes. Typing a brezza:// address searches
        // for it like any other text: no panel opens and the page keeps its content host.
        await NavigateThroughAddressBarAsync("brezza://history");
        await WaitForAsync(() => !string.IsNullOrEmpty(_tabs?.ActiveTab?.Url), TimeSpan.FromSeconds(5));
        if (_panelMode is not null)
        {
            throw new InvalidOperationException("A brezza:// address opened a browser panel: " + _panelMode);
        }

        var searched = _tabs?.ActiveTab?.Url ?? string.Empty;
        if (searched.StartsWith("brezza:", StringComparison.OrdinalIgnoreCase) ||
            searched.IndexOf("brezza", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("A brezza:// address was not searched: " + searched);
        }

        // The search has no network to reach; dismiss its notice so later steps start from a clean
        // notice area.
        if (_window.IsNoticeVisible)
        {
            _window.InvokeCommand(MainWindow.IdNoticeDismiss);
            await WaitForAsync(() => !_window.IsNoticeVisible, TimeSpan.FromSeconds(5));
        }

        // 5. A remote document gets no command bridge and no browser assets. The probe posts a
        // "newTab" command through the page messaging API; a browser that acted on it would open a
        // tab, so the tab count and the open panel are the real evidence.
        var probeUri = new Uri(Path.Combine(Path.GetDirectoryName(pageUri.LocalPath)!, "trust-probe.html")).AbsoluteUri;
        await NavigateThroughAddressBarAsync(probeUri);
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { } probeHost &&
            string.Equals(probeHost.Source, probeUri, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));
        var probeReport = await WaitForProbeReportAsync(settled: false);
        if (_tabs?.Tabs.Count != 1 || _panelMode is not null ||
            !string.Equals(_runtime?.ActiveHost?.Source, probeUri, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A remote document used a browser command: " + probeReport);
        }

        // Browser UI assets must stay unreadable for a remote document.
        probeReport = await WaitForProbeReportAsync(settled: true);
        if (probeReport.IndexOf("rejected", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("A remote document read browser UI assets: " + probeReport);
        }
        // The probe then navigates itself into a browser-owned page. No browser origin is served
        // over HTTP, so the attempt fails as an ordinary network error and no browser page renders.
        await WaitForAsync(() =>
            !string.Equals(_runtime?.ActiveHost?.Source, probeUri, StringComparison.OrdinalIgnoreCase) ||
            (_runtime?.ActiveHost?.Title ?? string.Empty).Length > 0, TimeSpan.FromSeconds(15));
        var finalTitle = _runtime?.ActiveHost?.Title ?? string.Empty;
        if (string.Equals(finalTitle, _localization.Get("settings.title"), StringComparison.Ordinal) ||
            string.Equals(finalTitle, _localization.Get("history.title"), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A remote document rendered a browser-owned page: " + finalTitle);
        }

        // 6. Return to the expected page so the saved session can be verified.
        await NavigateThroughAddressBarAsync(pageUri.AbsoluteUri);
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { } host &&
            string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));

        // 7. Tabs: create through the native button, switch through the native tab strip.
        ClickButton(MainWindow.IdNewTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 2 && _tabs.ActiveTab?.Id == "tab-2", TimeSpan.FromSeconds(10));
        if (_window.TabCount != 2)
        {
            throw new InvalidOperationException("The native tab strip did not receive the new tab: " + _window.TabCount);
        }

        var strip = _window.GetControl(MainWindow.IdTabs);
        NativeMethods.SendMessage(strip, NativeMethods.WM_MOUSEWHEEL,
            new IntPtr(unchecked((int)(NativeMethods.WHEEL_DELTA << 16))), IntPtr.Zero);
        await WaitForAsync(() => _tabs?.ActiveTab?.Id == "tab-1", TimeSpan.FromSeconds(10));
        NativeMethods.SendMessage(strip, NativeMethods.WM_MOUSEWHEEL,
            new IntPtr(unchecked((int)(-NativeMethods.WHEEL_DELTA << 16))), IntPtr.Zero);
        await WaitForAsync(() => _tabs?.ActiveTab?.Id != "tab-1", TimeSpan.FromSeconds(10));

        ClickButton(MainWindow.IdCloseActiveTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 1 && _tabs.ActiveTab?.Id == "tab-1", TimeSpan.FromSeconds(10));
        ClickButton(MainWindow.IdNewTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 2 && string.IsNullOrEmpty(_tabs.ActiveTab?.Url), TimeSpan.FromSeconds(10));

        await NavigateThroughAddressBarAsync(pageUri.AbsoluteUri);
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { } host &&
            string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));

        SelectTab(0);
        await WaitForAsync(() => _tabs?.ActiveTab?.Id == "tab-1", TimeSpan.FromSeconds(10));
        if (_window.SelectedTabIndex != 0)
        {
            throw new InvalidOperationException("The native tab strip did not follow the active tab: " + _window.SelectedTabIndex);
        }

        // 8. A missing page reports a failure notice instead of staying silent.
        var missingUri = new Uri(Path.Combine(Path.GetDirectoryName(pageUri.LocalPath)!, "missing.html"));
        await NavigateThroughAddressBarAsync(missingUri.AbsoluteUri);
        await WaitForAsync(() => _window.IsNoticeVisible, TimeSpan.FromSeconds(20));
        if (_window.NoticeText.IndexOf("无法加载", StringComparison.Ordinal) < 0 &&
            _window.NoticeText.IndexOf("could not be loaded", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("The native notice area did not report the failed page: " + _window.NoticeText);
        }
        if (string.Equals(Environment.GetEnvironmentVariable("BREZZA_SMOKE_LONG_NOTICE"), "1", StringComparison.Ordinal))
        {
            SendNotice(PageFailedNotice, new string('x', 320));
        }
        await PauseForVisualInspectionAsync();
        await AssertNoticeReloadRecoversAsync(missingUri);

        // 9. Close a tab by middle-clicking its native strip item.
        await NavigateThroughAddressBarAsync(pageUri.AbsoluteUri);
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { } host &&
            string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));
        MiddleClickTab(1);
        await WaitForAsync(() => _tabs?.Tabs.Count == 1 && _tabs.ActiveTab?.Id == "tab-1", TimeSpan.FromSeconds(10));
        if (_window.TabCount != 1)
        {
            throw new InvalidOperationException("Middle-click close did not remove the native tab: " + _window.TabCount);
        }

        // 10. Private tabs share one isolated profile and remove it when the last one closes.
        _window.InvokeCommand(MainWindow.IdNewPrivateTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 2 && _tabs.ActiveTab?.SessionKind == BrowserSessionKind.Private, TimeSpan.FromSeconds(10));
        await NavigateThroughAddressBarAsync(pageUri.AbsoluteUri);
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { } host &&
            string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));
        var privateProfile = _hostFactory?.PrivateUserDataFolder ??
            throw new InvalidOperationException("Private profile folder was not created.");
        await WaitForAsync(() => Directory.Exists(privateProfile), TimeSpan.FromSeconds(10));

        _window.InvokeCommand(MainWindow.IdNewPrivateTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == 3 && _tabs.ActiveTab?.SessionKind == BrowserSessionKind.Private, TimeSpan.FromSeconds(10));
        if (!string.Equals(_hostFactory?.PrivateUserDataFolder, privateProfile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Tabs in one private session did not share its isolated profile.");
        }

        await NavigateThroughAddressBarAsync(pageUri.AbsoluteUri);
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { } host &&
            string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));

        MiddleClickTab(2);
        await WaitForAsync(() => _tabs?.Tabs.Count == 2 && Directory.Exists(privateProfile), TimeSpan.FromSeconds(10));
        MiddleClickTab(1);
        await WaitForAsync(() => _tabs?.Tabs.Count == 1 && !Directory.Exists(privateProfile), TimeSpan.FromSeconds(20));

        // 11. Leave the session on a normal page so restore can be verified.
        await NavigateThroughAddressBarAsync(pageUri.AbsoluteUri);
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { } host &&
            string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));

        // 12. A crowded strip must not cost the user the close button, and it must not shrink tabs
        // into slivers either: tabs stop at the minimum width and the strip scrolls instead, with no
        // visible scroll control and without the tab control's own system scroll arrows.
        const int crowdedTabs = 30;

        for (var extra = 1; extra < crowdedTabs; extra++)
        {
            ClickButton(MainWindow.IdNewTab);
            await WaitForAsync(() => _tabs?.Tabs.Count == extra + 1, TimeSpan.FromSeconds(10));
        }

        var tabStrip = _window.GetControl(MainWindow.IdTabs);

        // Tabs stop shrinking: at this count the old sizing would have squeezed every tab to a
        // fraction of the minimum width to make them all fit. The minimum is what a tab's leading
        // icon and its close button need, so a tab at it still shows both and only ellipsizes its
        // label into the room that is left.
        var activeItem = new NativeMethods.RECT();
        NativeMethods.SendMessage(tabStrip, NativeMethods.TCM_GETITEMRECT,
            new IntPtr(_window.SelectedTabIndex), ref activeItem);
        if (activeItem.Width < _window.MinimumTabWidth)
        {
            throw new InvalidOperationException("Tabs kept shrinking past the minimum width with " +
                _window.TabCount + " tabs open: " + activeItem.Width + "px.");
        }

        // The strip is therefore wider than the window's client area, so its tabs cannot all be
        // showing: the strip scrolls.
        NativeMethods.GetWindowRect(tabStrip, out var stripBox);
        NativeMethods.GetClientRect(_window.Handle, out var client);
        if (stripBox.Width <= client.Width)
        {
            throw new InvalidOperationException("A crowded tab strip is not wider than the window.");
        }

        // The tab control's own scroll arrows are a system control that ignores the theme. The
        // strip is sized so its items never overflow, so the arrows must never be built at all:
        // a control that is only hidden after the fact still exists, and any later repaint can
        // show it. Existence, not visibility, is the check.
        var systemScroller = NativeMethods.FindWindowEx(tabStrip, IntPtr.Zero, "msctls_updown32", null);
        if (systemScroller != IntPtr.Zero)
        {
            throw new InvalidOperationException("The system tab scroll arrows exist with " +
                _window.TabCount + " tabs open.");
        }

        // The active tab was scrolled into view as it opened, so its close button is reachable.
        if (!_window.IsControlVisible(MainWindow.IdCloseActiveTab))
        {
            throw new InvalidOperationException(
                "The active-tab close button is hidden with " + _window.TabCount + " tabs open.");
        }

        AssertCloseButtonOnActiveTab();

        // Scrolling follows the active tab: the wheel moves the selection and the strip follows it.
        NativeMethods.SendMessage(tabStrip, NativeMethods.WM_MOUSEWHEEL,
            new IntPtr(unchecked((int)(NativeMethods.WHEEL_DELTA << 16))), IntPtr.Zero);
        await WaitForAsync(() => _tabs?.ActiveTab?.Id != "tab-" + crowdedTabs, TimeSpan.FromSeconds(10));
        AssertCloseButtonOnActiveTab();

        ClickButton(MainWindow.IdCloseActiveTab);
        await WaitForAsync(() => _tabs?.Tabs.Count == crowdedTabs - 1, TimeSpan.FromSeconds(10));
        // Close the rest through the same button, so the session is saved from one tab again.
        for (var expected = crowdedTabs - 2; expected >= 1; expected--)
        {
            ClickButton(MainWindow.IdCloseActiveTab);
            await WaitForAsync(() => _tabs?.Tabs.Count == expected, TimeSpan.FromSeconds(10));
        }

        // 13. Cookies and browsing data are toolbar panels with their own tables. The local test
        // page plants two cookies on the test origin, so the cookies panel has rows to list, remove
        // one by one, and clear; the browsing-data panel lists the four data categories.
        await AssertCookiesPanelAsync();
        await AssertBrowsingDataPanelAsync();

        // Leave the session on the ordinary smoke page again, so the saved session can be verified.
        await NavigateThroughAddressBarAsync(pageUri.AbsoluteUri);
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { } host &&
            string.Equals(host.Source, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));
    }

    // ---------- Waiting ----------

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for native browser state.");
    }

    private static async Task PauseForVisualInspectionAsync()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("BREZZA_SMOKE_PAUSE_AFTER_FAILURE_MS"), out var milliseconds) ||
            milliseconds <= 0)
        {
            return;
        }

        await Task.Delay(milliseconds);
    }

    private static async Task PauseForPanelInspectionAsync()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("BREZZA_SMOKE_PAUSE_AFTER_SETTINGS_MS"), out var milliseconds) ||
            milliseconds <= 0)
        {
            return;
        }

        await Task.Delay(milliseconds);
    }

    private static async Task PauseForHistoryInspectionAsync()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("BREZZA_SMOKE_PAUSE_AFTER_HISTORY_MS"), out var milliseconds) ||
            milliseconds <= 0)
        {
            return;
        }

        await Task.Delay(milliseconds);
    }

    private static async Task PauseForBookmarksInspectionAsync()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("BREZZA_SMOKE_PAUSE_AFTER_BOOKMARKS_MS"), out var milliseconds) ||
            milliseconds <= 0)
        {
            return;
        }

        await Task.Delay(milliseconds);
    }

    private static async Task WaitForAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for native browser state.");
    }

    private static void WriteSmokeResult(string? path, string result)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        File.WriteAllText(path, result);
    }
}
#endif
