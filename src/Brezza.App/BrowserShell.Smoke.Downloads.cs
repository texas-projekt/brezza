#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Brezza.App.Native;
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
}
#endif
