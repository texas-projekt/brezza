#if DEBUG
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Brezza.App.Native;
using Brezza.Core.Tabs;

// Debug-only smoke helpers for the native panels: finding, opening, driving, and asserting
// the settings, history, bookmark, cookies, and browsing-data panels.
namespace Brezza.App;

internal sealed partial class BrowserShell
{

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

    /// <summary>
    /// Waits for the modal confirmation this browser process owns. Other applications can show
    /// dialogs of the same class, so every dialog of the class is walked until one of them belongs
    /// to this process: FindWindowEx hands back only the first match, and a dialog another
    /// application opened earlier in the z-order would otherwise hide the browser's own from this
    /// search for the whole wait.
    /// </summary>
    /// <summary>
    /// The modal dialog this browser process owns, or zero when there is none. Other applications
    /// can show dialogs of the same class, so every dialog of the class is walked until one of them
    /// belongs to this process: FindWindowEx hands back only the first match, and a dialog another
    /// application opened earlier in the z-order would otherwise hide the browser's own from this
    /// search.
    /// </summary>
    private IntPtr FindOwnedDialog()
    {
        var current = IntPtr.Zero;
        while ((current = NativeMethods.FindWindowEx(IntPtr.Zero, current, "#32770", null)) != IntPtr.Zero)
        {
            if (NativeMethods.IsOwnedByProcess(current, _window.Handle)) return current;
        }

        return IntPtr.Zero;
    }

    private async Task<IntPtr> WaitForOwnedDialogAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var owned = FindOwnedDialog();
            if (owned != IntPtr.Zero) return owned;
            await Task.Delay(50);
        }

        return IntPtr.Zero;
    }

    /// <summary>True while the dialog handle still names a live window.</summary>
    private static bool DialogStillOpen(IntPtr dialog) => NativeMethods.IsWindow(dialog);

    private async Task OpenPanelAndWaitAsync(string mode)
    {
        var command = mode switch
        {
            "settings" => MainWindow.IdSettings,
            "history" => MainWindow.IdHistory,
            "bookmarks" => MainWindow.IdBookmarks,
            "cookies" => MainWindow.IdCookies,
            "downloads" => MainWindow.IdDownloads,
            _ => MainWindow.IdBrowsingData,
        };
        _window.InvokeCommand(command);
        await WaitForAsync(() => string.Equals(_panelMode, mode, StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        if (await FindPanelWindowAsync() == IntPtr.Zero)
        {
            throw new InvalidOperationException("The native " + mode + " panel window was not created.");
        }
    }

    /// <summary>Types into the open panel's search field and waits for the row count it leaves.</summary>
    private async Task AssertPanelFilterLeavesAsync(string filter, int expected)
    {
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The native panel is not open.");
        NativeMethods.SetWindowText(NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdFilter), filter);
        await WaitForAsync(async () => await PanelListCountAsync() == expected, TimeSpan.FromSeconds(10));
    }

    private async Task<int> PanelListCountAsync()
    {
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The native panel is not open.");
        var list = NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdList);
        return (int)NativeMethods.SendMessage(list, NativeMethods.LB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Selects a row of the open panel's list, the way the keyboard does.</summary>
    private async Task SelectPanelRowAsync(int index)
    {
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The native panel is not open.");
        NativeMethods.SendMessage(NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdList),
            NativeMethods.LB_SETCURSEL, new IntPtr(index), IntPtr.Zero);
        await Task.Yield();
    }

    /// <summary>Presses a key on the open panel's list, which owns the row actions.</summary>
    /// <remarks>
    /// The key is posted, not sent: a row action can open a modal confirmation, and a send would
    /// not return until that dialog closes, which the smoke itself has to dismiss.
    /// </remarks>
    private async Task PressPanelListKeyAsync(int virtualKey)
    {
        var panel = await FindPanelWindowAsync();
        if (panel == IntPtr.Zero) throw new InvalidOperationException("The native panel is not open.");
        NativeMethods.PostMessage(NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdList),
            NativeMethods.WM_KEYDOWN, new IntPtr(virtualKey), IntPtr.Zero);
        await Task.Yield();
    }

    /// <summary>Clicks Yes on the modal confirmation this browser process owns.</summary>
    private async Task ConfirmDialogAsync(IntPtr dialog)
    {
        var yes = NativeMethods.GetDlgItem(dialog, NativeMethods.IDYES);
        if (yes == IntPtr.Zero) throw new InvalidOperationException("The confirmation offers no Yes action.");
        NativeMethods.SendMessage(yes, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        await WaitForAsync(() => !DialogStillOpen(dialog), TimeSpan.FromSeconds(5));
    }

    /// <summary>Waits for the notice area to show the localized text of a key.</summary>
    private async Task WaitForNoticeAsync(string key)
    {
        var expected = _localization.Get(key);
        await WaitForAsync(() =>
            _window.IsNoticeVisible && _window.NoticeText.IndexOf(expected, StringComparison.Ordinal) >= 0,
            TimeSpan.FromSeconds(20));
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
        var expected = _localization.Get(mode switch
        {
            "history" => "history.title",
            "downloads" => "downloads.title",
            _ => "bookmarks.title",
        });
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
        if (text.IndexOf("127.0.0.1", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("The settings panel does not show the configured search provider: " + text);
        }

        var language = NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdLanguage);
        var languageText = NativeMethods.GetWindowText(language);
        var expectedLanguage = _localization.Get("settings.languageSystem");
        var acceptsEnglish = languageText.IndexOf(_localization.Get("settings.languageEnglish"), StringComparison.Ordinal) >= 0;
        var acceptsChinese = languageText.IndexOf(_localization.Get("settings.languageChinese"), StringComparison.Ordinal) >= 0;
        var acceptsTraditional = languageText.IndexOf(_localization.Get("settings.languageTraditional"), StringComparison.Ordinal) >= 0;
        if (languageText.IndexOf(expectedLanguage, StringComparison.Ordinal) < 0 && !acceptsEnglish && !acceptsChinese && !acceptsTraditional)
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
}
#endif
