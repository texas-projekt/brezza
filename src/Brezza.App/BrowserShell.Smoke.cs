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
/// removed HTML bridge.
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
            WriteSmokeResult(resultPath, "PASS: native controls, tabs, favicons, private cleanup, failure state, internal panel, and remote trust boundary.");
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
        // shell decoded the image the host fetched, ready for the strip to paint.
        await WaitForAsync(() =>
            _runtime?.ActiveHost is { FaviconUri.Length: > 0 } host &&
            host.FaviconUri.StartsWith("data:image/png;base64,", StringComparison.Ordinal),
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

        // 4. An internal route opens its native panel instead of a content host.
        await NavigateThroughAddressBarAsync("brezza://history");
        await WaitForAsync(() => string.Equals(_panelMode, "history", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        await AssertPanelListCountAsync(1);
        if (_runtime?.ActiveHost is not null)
        {
            throw new InvalidOperationException("An internal route created a WebView2 content host.");
        }

        await ClosePanelAndWaitAsync();
        await WaitForAsync(() => string.IsNullOrEmpty(_tabs?.ActiveTab?.Url), TimeSpan.FromSeconds(10));

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
        // strip is sized so its items never overflow, so they must never appear at all.
        var systemScroller = NativeMethods.FindWindowEx(tabStrip, IntPtr.Zero, "msctls_updown32", null);
        if (systemScroller != IntPtr.Zero && NativeMethods.IsWindowVisible(systemScroller))
        {
            throw new InvalidOperationException("The system tab scroll arrows are visible with " +
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
    }

    /// <summary>The active tab's close button sits inside that tab's own edges, however narrow it is.</summary>
    private void AssertCloseButtonOnActiveTab()
    {
        var closeButton = _window.GetControl(MainWindow.IdCloseActiveTab);
        NativeMethods.GetWindowRect(closeButton, out var closeBox);
        var tabStrip = _window.GetControl(MainWindow.IdTabs);
        var activeItem = new NativeMethods.RECT();
        NativeMethods.SendMessage(tabStrip, NativeMethods.TCM_GETITEMRECT,
            new IntPtr(_window.SelectedTabIndex), ref activeItem);
        NativeMethods.GetWindowRect(tabStrip, out var stripBox);
        if (closeBox.Width < 14 || closeBox.Height < 14)
        {
            throw new InvalidOperationException("The close button fell below a clickable size with " +
                _window.TabCount + " tabs open: " + closeBox.Width + "x" + closeBox.Height + ".");
        }

        // The strip has no border, so an item rectangle offsets straight from its screen origin.
        var closeCenter = (closeBox.left + closeBox.right) / 2;
        if (closeCenter < stripBox.left + activeItem.left || closeCenter > stripBox.left + activeItem.right)
        {
            throw new InvalidOperationException("The close button left the active tab with " +
                _window.TabCount + " tabs open.");
        }

        // The strip scrolled the active tab into view, so the close button is on screen and clear
        // of the new-tab button rather than hiding under it.
        NativeMethods.GetWindowRect(_window.GetControl(MainWindow.IdNewTab), out var newTabBox);
        if (closeBox.right > newTabBox.left)
        {
            throw new InvalidOperationException("The close button reaches past the tab band with " +
                _window.TabCount + " tabs open.");
        }
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

    // ---------- Native input helpers ----------

    private void ClickButton(int controlId)
    {
        var control = _window.GetControl(controlId);
        if (control == IntPtr.Zero)
        {
            throw new InvalidOperationException("The native control " + controlId + " does not exist.");
        }

        NativeMethods.SendMessage(
            _window.Handle,
            NativeMethods.WM_COMMAND,
            new IntPtr(NativeMethods.MakeLong(controlId, NativeMethods.BN_CLICKED)),
            control);
    }

    /// <summary>
    /// Selects a tab the way a user does. A tab control does not notify for TCM_SETCURSEL, so the
    /// smoke drives the real input path: a left click on the tab strip item.
    /// </summary>
    private void SelectTab(int index)
    {
        var strip = _window.GetControl(MainWindow.IdTabs);
        var rect = new NativeMethods.RECT();
        NativeMethods.SendMessage(strip, NativeMethods.TCM_GETITEMRECT, new IntPtr(index), ref rect);
        var position = new IntPtr(NativeMethods.MakeLong(rect.left + 6, rect.top + Math.Max(1, rect.Height / 2)));
        NativeMethods.SendMessage(strip, NativeMethods.WM_LBUTTONDOWN, new IntPtr(0x0001), position);
        NativeMethods.SendMessage(strip, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, position);
    }
    private void MiddleClickTab(int index)
    {
        var strip = _window.GetControl(MainWindow.IdTabs);
        var rect = new NativeMethods.RECT();
        NativeMethods.SendMessage(strip, NativeMethods.TCM_GETITEMRECT, new IntPtr(index), ref rect);
        var position = new IntPtr(NativeMethods.MakeLong(rect.left + 6, rect.top + Math.Max(1, rect.Height / 2)));
        NativeMethods.SendMessage(strip, NativeMethods.WM_MBUTTONDOWN, new IntPtr(0x0010), position);
        NativeMethods.SendMessage(strip, NativeMethods.WM_MBUTTONUP, IntPtr.Zero, position);
    }

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
    // ---------- Panel helpers ----------

    private async Task<IntPtr> FindPanelWindowAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var panel = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, InternalPanelWindow.WindowClassName, null);
            if (panel != IntPtr.Zero) return panel;
            await Task.Delay(50);
        }

        return IntPtr.Zero;
    }

    /// <summary>Waits for a top-level window of a class to appear, for example a modal dialog.</summary>
    private static async Task<IntPtr> WaitForWindowAsync(string className, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, className, null);
            if (found != IntPtr.Zero) return found;
            await Task.Delay(50);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Waits for the modal confirmation this browser process owns. Other applications can show
    /// dialogs of the same class, so the window must belong to this process.
    /// </summary>
    private async Task<IntPtr> WaitForOwnedDialogAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "#32770", null);
            if (found != IntPtr.Zero && NativeMethods.IsOwnedByProcess(found, _window.Handle)) return found;
            await Task.Delay(50);
        }

        return IntPtr.Zero;
    }

    /// <summary>True while the dialog handle still names a live window.</summary>
    private static bool DialogStillOpen(IntPtr dialog) => NativeMethods.IsWindow(dialog);

    private async Task OpenPanelAndWaitAsync(string mode)
    {
        _window.InvokeCommand(mode == "settings" ? MainWindow.IdSettings : mode == "history" ? MainWindow.IdHistory : MainWindow.IdBookmarks);
        await WaitForAsync(() => string.Equals(_panelMode, mode, StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        if (await FindPanelWindowAsync() == IntPtr.Zero)
        {
            throw new InvalidOperationException("The native " + mode + " panel window was not created.");
        }
    }

    /// <summary>
    /// Clears history through the panel's own action and its modal confirmation, then closes the
    /// panel. Closing a panel after a modal dialog is the case where Windows stops returning focus
    /// to the browser, so the shell has to do it.
    /// </summary>
    private async Task AssertPanelCloseReturnsFocusAsync()
    {
        await OpenPanelAndWaitAsync("history");
        var panel = await FindPanelWindowAsync();

        // The Clear action opens a modal confirmation owned by the panel. The message is posted,
        // not sent: MessageBox runs its own modal loop and a send would not return until it closes.
        NativeMethods.PostMessage(panel, NativeMethods.WM_COMMAND,
            new IntPtr(InternalPanelWindow.IdClear), NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdClear));
        var dialog = await WaitForOwnedDialogAsync(TimeSpan.FromSeconds(5));
        if (dialog == IntPtr.Zero) throw new InvalidOperationException("Clearing history opened no confirmation.");
        var yes = NativeMethods.GetDlgItem(dialog, NativeMethods.IDYES);
        if (yes == IntPtr.Zero) throw new InvalidOperationException("The confirmation offers no Yes action.");
        NativeMethods.SendMessage(yes, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        await WaitForAsync(() => !DialogStillOpen(dialog), TimeSpan.FromSeconds(5));

        NativeMethods.SendMessage(panel, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        await WaitForAsync(() => _panelMode is null, TimeSpan.FromSeconds(10));

        // The browser window must hold the thread's active window again; the panel is gone.
        var active = NativeMethods.ActiveWindowOfThread(_window.Handle);
        if (active != _window.Handle)
        {
            throw new InvalidOperationException(
                "Closing the panel left the browser without focus: active window is 0x" + active.ToInt64().ToString("X") +
                ", expected the main window 0x" + _window.Handle.ToInt64().ToString("X"));
        }
    }

    private async Task ClosePanelAndWaitAsync()
    {
        var panel = await FindPanelWindowAsync();
        if (panel != IntPtr.Zero)
        {
            NativeMethods.SendMessage(panel, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        await WaitForAsync(() => _panelMode is null, TimeSpan.FromSeconds(10));
    }

    /// <summary>Verifies the native panel shows the localized title of the requested page.</summary>
    private async Task AssertPanelTitleAsync(string mode)
    {
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The native " + mode + " panel is not open.");
        var title = NativeMethods.GetWindowText(NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdTitle));
        var expected = _localization.Get(mode == "history" ? "history.title" : "bookmarks.title");
        if (!string.Equals(title, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The native " + mode + " panel title is '" + title + "', expected '" + expected + "'.");
        }
    }

    private async Task AssertPanelSettingsAsync()
    {
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The settings panel is not open.");
        var template = NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdTemplate);
        var text = NativeMethods.GetWindowText(template);
        if (text.IndexOf("bing.com/search", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("The settings panel does not show the configured search provider: " + text);
        }

        var language = NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdLanguage);
        var languageText = NativeMethods.GetWindowText(language);
        var expectedLanguage = _localization.Get("settings.languageSystem");
        var acceptsEnglish = languageText.IndexOf(_localization.Get("settings.languageEnglish"), StringComparison.Ordinal) >= 0;
        var acceptsChinese = languageText.IndexOf(_localization.Get("settings.languageChinese"), StringComparison.Ordinal) >= 0;
        if (languageText.IndexOf(expectedLanguage, StringComparison.Ordinal) < 0 && !acceptsEnglish && !acceptsChinese)
        {
            throw new InvalidOperationException("The settings panel does not show a supported language: " + languageText);
        }
    }

    private async Task AssertPanelListCountAsync(int expected)
    {
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The native panel is not open.");
        var list = NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdList);
        var count = (int)NativeMethods.SendMessage(list, NativeMethods.LB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count != expected)
        {
            throw new InvalidOperationException("The native panel list holds " + count + " item(s), expected " + expected + ".");
        }
    }

    private async Task AssertPanelEmptyStateAsync(bool visible)
    {
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The native panel is not open.");
        var empty = NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdEmpty);
        var actual = empty != IntPtr.Zero && NativeMethods.IsWindowVisible(empty);
        if (actual != visible)
        {
            throw new InvalidOperationException(
                "The native panel empty-state visibility was " + actual + ", expected " + visible + ".");
        }
    }

    private void AssertActiveTabFinishedLoading()
    {
        var index = _window.SelectedTabIndex;
        if (index < 0) throw new InvalidOperationException("The native tab strip has no active item.");
        var text = _window.GetTabText(index);
        if (text.EndsWith("...", StringComparison.Ordinal))
            throw new InvalidOperationException("The active tab still shows a loading marker after navigation completed: " + text);
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
