#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Brezza.App.Native;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;

// Debug-only smoke helpers for downloads: a real attachment from the local test origin, the file
// the adapter writes, and the downloads panel that lists, filters, removes, and clears it.
namespace Brezza.App;

internal sealed partial class BrowserShell
{
    /// <summary>
    /// Drives a download end to end through the real runtime: the local origin serves an
    /// attachment, the browser writes it to the downloads folder, records it, and the downloads
    /// panel lists it. The panel's own row action removes one entry and its list action clears the
    /// rest behind a confirmation; neither touches the files. One download is left recorded, so the
    /// next process can verify the list is restored from the store.
    /// </summary>
    private async Task AssertDownloadsPanelAsync()
    {
        var downloadUrl = Environment.GetEnvironmentVariable("BREZZA_SMOKE_DOWNLOAD_PAGE");
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException("The smoke download page URL is missing.");
        }

        // Every file this run writes is tracked here, so a failure still leaves the real downloads
        // folder as it was.
        var writtenFiles = new List<string>();
        try
        {
            // Two downloads of the same attachment: the adapter de-duplicates the second file name,
            // so the panel has one row to remove and one row left for the list-level clear.
            for (var expected = 1; expected <= 2; expected++)
            {
                await NavigateThroughAddressBarAsync(downloadUrl);
                await WaitForAsync(() => _downloads?.Downloads.Count(item => item.IsTerminal) >= expected,
                    TimeSpan.FromSeconds(20));
                writtenFiles.Add(_downloads!.Downloads.First(item => item.IsTerminal).TargetFilePath);
                await WaitForNoticeAsync("notice.downloadCompleted");
            }

            // The files the adapter resolved are really written, not only listed.
            foreach (var file in writtenFiles)
            {
                if (!File.Exists(file))
                {
                    throw new InvalidOperationException("The downloaded file is missing: " + file);
                }
            }

            await OpenPanelAndWaitAsync("downloads");
            await AssertPanelTitleAsync("downloads");
            await AssertPanelListCountAsync(2);
            await AssertPanelEmptyStateAsync(visible: false);

            // The search field narrows the list to the matching row and back.
            await AssertPanelFilterLeavesAsync(_downloads!.Downloads[0].FileName, 1);
            await AssertPanelFilterLeavesAsync("no-such-download", 0);
            await AssertPanelFilterLeavesAsync(string.Empty, 2);

            // The row's own action removes that entry without asking and leaves the file.
            await SelectPanelRowAsync(0);
            await PressPanelListKeyAsync(NativeMethods.VK_DELETE);
            await WaitForAsync(() => _downloads!.Downloads.Count == 1, TimeSpan.FromSeconds(10));
            await AssertPanelListCountAsync(1);

            // The list-level action clears what is left behind a confirmation. The message is
            // posted, not sent: the confirmation runs its own modal loop.
            var panel = await FindPanelWindowAsync();
            NativeMethods.PostMessage(panel, NativeMethods.WM_COMMAND,
                new IntPtr(InternalPanelWindow.IdClear), NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdClear));
            var dialog = await WaitForOwnedDialogAsync(TimeSpan.FromSeconds(5));
            if (dialog == IntPtr.Zero)
            {
                throw new InvalidOperationException("Clearing downloads opened no confirmation.");
            }

            await ConfirmDialogAsync(dialog);
            await WaitForAsync(() => _downloads!.Downloads.Count == 0, TimeSpan.FromSeconds(10));
            await AssertPanelListCountAsync(0);
            await AssertPanelEmptyStateAsync(visible: true);
            await ClosePanelAndWaitAsync();

            foreach (var file in writtenFiles)
            {
                if (!File.Exists(file))
                {
                    throw new InvalidOperationException("Clearing the list deleted a downloaded file: " + file);
                }
            }

            // One download is left recorded, so the durable list is not empty when this process
            // saves it at shutdown and the next one restores it.
            await NavigateThroughAddressBarAsync(downloadUrl);
            await WaitForAsync(() => _downloads!.Downloads.Count == 1, TimeSpan.FromSeconds(20));
            writtenFiles.Add(_downloads!.Downloads[0].TargetFilePath);
            await WaitForNoticeAsync("notice.downloadCompleted");
        }
        finally
        {
            // The browser downloads into the real downloads folder, so a run removes the files it
            // wrote instead of leaving them behind.
            foreach (var file in writtenFiles)
            {
                try { File.Delete(file); }
                catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// Verifies the download list was restored from the store in a later process. The list is
    /// durable browser state, so it comes back whatever the startup behavior is.
    /// </summary>
    private void AssertDownloadListRestored()
    {
        var restored = _downloads?.Downloads.Count ?? 0;
        if (restored < 1)
        {
            throw new InvalidOperationException("The download list was not restored from the store: " + restored + " entry(s).");
        }
    }

    /// <summary>
    /// Drives a running download through the panel's own controls. The slow attachment streams in
    /// chunks, so a download is still running when the panel pauses it, resumes it, and lets it
    /// finish; a second one is cancelled behind its confirmation. The row buttons are clicked the
    /// way a mouse clicks them, so the hit-testing the panel ships is what runs.
    /// </summary>
    private async Task AssertDownloadControlsAsync()
    {
        var slowUrl = Environment.GetEnvironmentVariable("BREZZA_SMOKE_SLOW_DOWNLOAD_PAGE");
        if (string.IsNullOrWhiteSpace(slowUrl))
        {
            throw new InvalidOperationException("The smoke slow download page URL is missing.");
        }

        var writtenFiles = new List<string>();
        try
        {
            // The running download reports the bytes that have arrived before it can be paused.
            await NavigateThroughAddressBarAsync(slowUrl);
            await WaitForAsync(() => _downloads!.Downloads.Any(item =>
                item.IsRunning && item.ReceivedBytes > 0), TimeSpan.FromSeconds(20));
            writtenFiles.Add(_downloads!.Downloads.First(item => item.IsRunning).TargetFilePath);

            await OpenPanelAndWaitAsync("downloads");
            await ClickPanelRowButtonAsync(PanelRowButton.Pause, 0);
            await WaitForAsync(() => _downloads!.Downloads.Any(item => item.State == BrowserDownloadState.Paused),
                TimeSpan.FromSeconds(10));

            // A paused download keeps its place in the list and the bytes it had arrived.
            var paused = _downloads!.Downloads.First(item => item.State == BrowserDownloadState.Paused);
            if (paused.ReceivedBytes is not > 0)
            {
                throw new InvalidOperationException("The paused download lost the bytes it had arrived.");
            }

            await ClickPanelRowButtonAsync(PanelRowButton.Resume, 0);
            await WaitForAsync(() => _downloads!.Downloads.Any(item => item.State == BrowserDownloadState.InProgress),
                TimeSpan.FromSeconds(10));
            await ClosePanelAndWaitAsync();

            await WaitForAsync(() => _downloads!.Downloads.Any(item => item.State == BrowserDownloadState.Completed),
                TimeSpan.FromSeconds(30));

            // A cancelled download ends interrupted, and its partial file is dropped.
            await NavigateThroughAddressBarAsync(slowUrl);
            await WaitForAsync(() => _downloads!.Downloads.Any(item => item.State == BrowserDownloadState.InProgress),
                TimeSpan.FromSeconds(20));
            writtenFiles.Add(_downloads!.Downloads.First(item => item.State == BrowserDownloadState.InProgress).TargetFilePath);

            await OpenPanelAndWaitAsync("downloads");
            await ClickPanelRowButtonAsync(PanelRowButton.Cancel, 0);
            var dialog = await WaitForOwnedDialogAsync(TimeSpan.FromSeconds(5));
            if (dialog == IntPtr.Zero)
            {
                throw new InvalidOperationException("Cancelling a download opened no confirmation.");
            }

            await ConfirmDialogAsync(dialog);
            await WaitForAsync(() => _downloads!.Downloads.Any(item =>
                item.State == BrowserDownloadState.Interrupted), TimeSpan.FromSeconds(10));
            await ClosePanelAndWaitAsync();
        }
        finally
        {
            // A cancelled download drops its own partial file, so only the finished one is left to
            // remove; the real downloads folder is left as it was.
            foreach (var file in writtenFiles)
            {
                try { File.Delete(file); }
                catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// Drives the settings panel's downloads-folder picker. The dialog is modal on the shell's own
    /// thread, so a pool timer accepts it while that thread sits inside the dialog's message loop.
    /// A picker that cannot be shown is a failure: the browser used to fault on the way into it,
    /// which took the whole browser down.
    /// </summary>
    private async Task AssertDownloadFolderPickerAsync()
    {
        await OpenPanelAndWaitAsync("settings");
        var panel = await FindPanelWindowAsync();
        var browse = NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdDownloadFolderBrowse);
        if (browse == IntPtr.Zero)
        {
            throw new InvalidOperationException("The settings panel has no downloads-folder browse button.");
        }

        var folderField = NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdDownloadFolder);
        if (folderField == IntPtr.Zero)
        {
            throw new InvalidOperationException("The settings panel has no downloads-folder field.");
        }

        // The dialog is modal on this thread, so a pool timer accepts it from another thread.
        // The wait itself runs off the UI thread: the posted command has to be dispatched, and
        // that is what puts this thread inside the dialog's message loop.
        using var accepted = new System.Threading.ManualResetEventSlim(false);
        using var timer = new System.Threading.Timer(
            _ => AcceptFolderDialog(accepted), null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);

        NativeMethods.PostMessage(panel, NativeMethods.WM_COMMAND,
            new IntPtr(InternalPanelWindow.IdDownloadFolderBrowse), browse);

        if (!await Task.Run(() => accepted.Wait(TimeSpan.FromSeconds(20))))
        {
            throw new InvalidOperationException("The downloads-folder picker never appeared.");
        }

        // The field is filled from what the picker handed back, so it is never left empty.
        await WaitForAsync(() => ReadFieldText(folderField).Length > 0, TimeSpan.FromSeconds(5));
        await ClosePanelAndWaitAsync();
    }

    /// <summary>Accepts the folder picker this process owns, from a thread that is not blocked in it.</summary>
    private void AcceptFolderDialog(System.Threading.ManualResetEventSlim accepted)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var dialog = FindOwnedDialog();
            if (dialog != IntPtr.Zero)
            {
                var ok = NativeMethods.GetDlgItem(dialog, NativeMethods.IDOK);
                if (ok != IntPtr.Zero)
                {
                    // Let the modern dialog finish laying out its controls before driving the
                    // button. Posting a command into a half-created dialog is what made this smoke
                    // step flaky.
                    System.Threading.Thread.Sleep(250);
                    if (DialogStillOpen(dialog) && NativeMethods.IsWindow(ok))
                    {
                        NativeMethods.PostMessage(ok, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        accepted.Set();
                        return;
                    }
                }
            }

            System.Threading.Thread.Sleep(100);
        }
    }

    private static string ReadFieldText(IntPtr field)
    {
        var text = new System.Text.StringBuilder(512);
        NativeMethods.GetWindowText(field, text, 512);
        return text.ToString().Trim();
    }

    /// <summary>Clicks a row button in the open panel, the way a mouse press and release does.</summary>
    private async Task ClickPanelRowButtonAsync(PanelRowButton button, int row)
    {
        var panel = await FindPanelWindowAsync();
        var list = NativeMethods.GetDlgItem(panel, InternalPanelWindow.IdList);

        var buffer = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeMethods.RECT>());
        try
        {
            NativeMethods.SendMessage(list, 0x0198 /* LB_GETITEMRECT */, new IntPtr(row), buffer);
            var rect = Marshal.PtrToStructure<NativeMethods.RECT>(buffer);

            // The buttons sit inside the right edge of the row, counted from the right.
            const int size = 28;
            const int inset = 10;
            const int gap = 4;
            var right = rect.right - inset - size / 2;
            var centre = button switch
            {
                PanelRowButton.Remove => right,
                PanelRowButton.Cancel or PanelRowButton.Folder => right - (size + gap),
                _ => right - 2 * (size + gap),
            };
            var point = new IntPtr(((rect.top + rect.Height / 2) << 16) | (centre & 0xFFFF));
            NativeMethods.PostMessage(list, NativeMethods.WM_LBUTTONDOWN, new IntPtr(1), point);
            NativeMethods.PostMessage(list, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, point);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }
}
#endif
