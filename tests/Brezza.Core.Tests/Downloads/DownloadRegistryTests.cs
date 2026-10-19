using System.Linq;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Tabs;

namespace Brezza.Core.Tests.Downloads;

public sealed class DownloadRegistryTests
{
    private static readonly DateTime Started = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_started_download_is_in_progress_and_listed_newest_first()
    {
        var registry = new DownloadRegistry();

        var first = registry.Start("https://example.com/one.bin", "one.bin", @"C:\Downloads\one.bin", BrowserSessionKind.Normal);
        var second = registry.Start("https://example.com/two.bin", "two.bin", @"C:\Downloads\two.bin", BrowserSessionKind.Normal);

        Assert.Equal(BrowserDownloadState.InProgress, first.State);
        Assert.Equal(new[] { second.Id, first.Id }, registry.Downloads.Select(item => item.Id));
    }

    [Fact]
    public void Starting_the_same_live_target_twice_keeps_one_entry()
    {
        var registry = new DownloadRegistry();

        var first = registry.Start("https://example.com/file.bin", "file.bin", @"C:\Downloads\file.bin", BrowserSessionKind.Normal);
        var second = registry.Start("https://example.com/file.bin", "file.bin", @"C:\Downloads\file.bin", BrowserSessionKind.Normal);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(registry.Downloads);
    }

    [Fact]
    public void Restoring_the_same_download_twice_keeps_one_entry()
    {
        var registry = new DownloadRegistry();
        var download = new BrowserDownload("download-stored01", "https://example.com/report.pdf", "report.pdf",
            @"C:\Downloads\report.pdf", BrowserDownloadState.Completed, Started, BrowserSessionKind.Normal);

        registry.Restore(download);
        registry.Restore(download);

        Assert.Single(registry.Downloads);
    }

    [Fact]
    public void A_completed_download_records_its_total_size_and_completion_time()
    {
        var registry = new DownloadRegistry();
        var download = registry.Start("https://example.com/report.pdf", "report.pdf", @"C:\Downloads\report.pdf", BrowserSessionKind.Normal);

        var completed = registry.Complete(@"C:\Downloads\report.pdf", 2048);

        Assert.NotNull(completed);
        Assert.Equal(download.Id, completed!.Id);
        Assert.Equal(BrowserDownloadState.Completed, completed.State);
        Assert.Equal(2048UL, completed.TotalBytes);
        Assert.NotNull(completed.CompletedUtc);
        Assert.True(completed.IsTerminal);
    }

    [Fact]
    public void An_interrupted_download_keeps_the_runtime_reason()
    {
        var registry = new DownloadRegistry();
        registry.Start("https://example.com/big.iso", "big.iso", @"C:\Downloads\big.iso", BrowserSessionKind.Normal);

        var interrupted = registry.Interrupt(@"C:\Downloads\big.iso", "NetworkError");

        Assert.NotNull(interrupted);
        Assert.Equal(BrowserDownloadState.Interrupted, interrupted!.State);
        Assert.Equal("NetworkError", interrupted.InterruptReason);
        Assert.True(interrupted.IsTerminal);
    }

    [Fact]
    public void A_terminal_status_that_arrives_before_the_start_is_applied_to_it()
    {
        var registry = new DownloadRegistry();

        // The runtime can report a completion for a download whose start event has not been
        // observed yet; the status waits for its start instead of being lost.
        registry.Interrupt(@"C:\Downloads\early.bin", "NetworkError");

        var download = registry.Start("https://example.com/early.bin", "early.bin", @"C:\Downloads\early.bin", BrowserSessionKind.Normal);

        Assert.Equal(BrowserDownloadState.Interrupted, download.State);
        Assert.Equal("NetworkError", download.InterruptReason);
        Assert.Single(registry.Downloads);
    }

    [Fact]
    public void A_status_for_an_unknown_download_is_ignored_after_the_start_arrives()
    {
        var registry = new DownloadRegistry();
        registry.Complete(@"C:\Downloads\unknown.bin", 10);

        var download = registry.Start("https://example.com/known.bin", "known.bin", @"C:\Downloads\known.bin", BrowserSessionKind.Normal);

        Assert.Equal(BrowserDownloadState.InProgress, download.State);
    }

    [Fact]
    public void A_private_download_is_flagged_and_dropped_with_its_session()
    {
        var registry = new DownloadRegistry();
        registry.Start("https://example.com/normal.bin", "normal.bin", @"C:\Downloads\normal.bin", BrowserSessionKind.Normal);
        registry.Start("https://example.com/private.bin", "private.bin", @"C:\Downloads\private.bin", BrowserSessionKind.Private);

        Assert.Equal(1, registry.RemovePrivateSession());

        var remaining = Assert.Single(registry.Downloads);
        Assert.Equal("normal.bin", remaining.FileName);
        Assert.Equal(0, registry.RemovePrivateSession());
    }

    [Fact]
    public void Entries_can_be_removed_one_by_one_or_cleared()
    {
        var registry = new DownloadRegistry();
        var first = registry.Start("https://example.com/one.bin", "one.bin", @"C:\Downloads\one.bin", BrowserSessionKind.Normal);
        registry.Start("https://example.com/two.bin", "two.bin", @"C:\Downloads\two.bin", BrowserSessionKind.Normal);

        Assert.True(registry.Remove(first.Id));
        Assert.False(registry.Remove(first.Id));
        Assert.Single(registry.Downloads);

        registry.Clear();
        Assert.Empty(registry.Downloads);
    }

    [Fact]
    public void A_restored_download_keeps_its_recorded_identity_and_state()
    {
        var registry = new DownloadRegistry();
        var stored = new BrowserDownload(
            "download-stored01", "https://example.com/old.pdf", "old.pdf", @"C:\Downloads\old.pdf",
            BrowserDownloadState.Completed, Started, BrowserSessionKind.Normal, Started.AddMinutes(1), 99);

        registry.Restore(stored);

        var restored = Assert.Single(registry.Downloads);
        Assert.Equal("download-stored01", restored.Id);
        Assert.Equal(BrowserDownloadState.Completed, restored.State);
        Assert.Equal(99UL, restored.TotalBytes);
        Assert.Equal(Started.AddMinutes(1), restored.CompletedUtc);
    }

    [Fact]
    public void Restored_entries_keep_the_order_the_store_gave_them()
    {
        // The store hands the entries newest first, so appending them keeps the list newest first.
        // Inserting each one at the front instead would reverse the restored list.
        var registry = new DownloadRegistry();
        registry.Restore(new BrowserDownload("download-new", "https://example.com/new.pdf", "new.pdf",
            @"C:\Downloads\new.pdf", BrowserDownloadState.Completed, Started.AddHours(2), BrowserSessionKind.Normal));
        registry.Restore(new BrowserDownload("download-old", "https://example.com/old.pdf", "old.pdf",
            @"C:\Downloads\old.pdf", BrowserDownloadState.Completed, Started, BrowserSessionKind.Normal));

        Assert.Equal(new[] { "download-new", "download-old" }, registry.Downloads.Select(item => item.Id));

        // A download that starts after a restore still leads the list.
        var live = registry.Start("https://example.com/live.bin", "live.bin", @"C:\Downloads\live.bin", BrowserSessionKind.Normal);
        Assert.Equal(live.Id, registry.Downloads[0].Id);
        Assert.Equal(new[] { "download-new", "download-old" }, registry.Downloads.Skip(1).Select(item => item.Id));
    }

    [Fact]
    public void A_running_download_records_the_bytes_that_have_arrived()
    {
        var registry = new DownloadRegistry();
        registry.Start("https://example.com/big.iso", "big.iso", @"C:\Downloads\big.iso", BrowserSessionKind.Normal);

        var progressed = registry.ReportProgress(@"C:\Downloads\big.iso", 4096);

        Assert.NotNull(progressed);
        Assert.Equal(4096UL, progressed!.ReceivedBytes);
        Assert.Equal(BrowserDownloadState.InProgress, progressed.State);
        Assert.Equal(4096UL, registry.Downloads[0].ReceivedBytes);
    }

    [Fact]
    public void A_paused_download_keeps_its_place_and_its_bytes_and_is_not_terminal()
    {
        var registry = new DownloadRegistry();
        registry.Start("https://example.com/big.iso", "big.iso", @"C:\Downloads\big.iso", BrowserSessionKind.Normal);
        registry.ReportProgress(@"C:\Downloads\big.iso", 4096);

        var paused = registry.Pause(@"C:\Downloads\big.iso");

        Assert.NotNull(paused);
        Assert.Equal(BrowserDownloadState.Paused, paused!.State);
        Assert.True(paused.IsRunning);
        // A paused download is not a finished record, so it is never persisted.
        Assert.False(paused.IsTerminal);
        Assert.Equal(4096UL, paused.ReceivedBytes);

        var resumed = registry.Resume(@"C:\Downloads\big.iso");

        Assert.NotNull(resumed);
        Assert.Equal(BrowserDownloadState.InProgress, resumed!.State);
        Assert.True(resumed.IsRunning);
        Assert.Equal(4096UL, resumed.ReceivedBytes);
    }

    [Fact]
    public void A_command_for_an_unknown_download_changes_nothing()
    {
        var registry = new DownloadRegistry();

        Assert.Null(registry.Pause(@"C:\Downloads\missing.iso"));
        Assert.Null(registry.Resume(@"C:\Downloads\missing.iso"));
        Assert.Null(registry.ReportProgress(@"C:\Downloads\missing.iso", 10));
    }

    [Fact]
    public void Every_mutation_reports_the_change()
    {
        var registry = new DownloadRegistry();
        var changes = 0;
        registry.Changed += (_, _) => changes++;

        var download = registry.Start("https://example.com/one.bin", "one.bin", @"C:\Downloads\one.bin", BrowserSessionKind.Normal);
        registry.Complete(@"C:\Downloads\one.bin", 1);
        registry.Remove(download.Id);

        Assert.Equal(3, changes);
    }
}
