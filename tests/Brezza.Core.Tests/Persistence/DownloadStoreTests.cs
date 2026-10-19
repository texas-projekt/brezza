using System;
using System.IO;
using System.Linq;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Tabs;
using Brezza.Persistence;

namespace Brezza.Core.Tests.Persistence;

public sealed class DownloadStoreTests
{
    [Fact]
    public void Persists_terminal_downloads_and_restores_them()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "downloads.xml");
        var store = new DownloadStore(path);
        var started = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        store.Save(new[]
        {
            new BrowserDownload("download-a", "https://example.com/report.pdf", "report.pdf",
                @"C:\Downloads\report.pdf", BrowserDownloadState.Completed, started, BrowserSessionKind.Normal,
                started.AddMinutes(1), 2048),
            new BrowserDownload("download-b", "https://example.com/big.iso", "big.iso",
                @"C:\Downloads\big.iso", BrowserDownloadState.Interrupted, started.AddMinutes(5), BrowserSessionKind.Normal,
                started.AddMinutes(6), null, "NetworkError"),
        });

        var restored = new DownloadStore(path).GetAll();

        Assert.Equal(2, restored.Count);
        var newest = restored[0];
        Assert.Equal("download-b", newest.Id);
        Assert.Equal(BrowserDownloadState.Interrupted, newest.State);
        Assert.Equal("NetworkError", newest.InterruptReason);
        Assert.Null(newest.TotalBytes);
        var completed = restored[1];
        Assert.Equal(2048UL, completed.TotalBytes);
        Assert.Equal(started.AddMinutes(1), completed.CompletedUtc);
    }

    [Fact]
    public void In_progress_and_private_downloads_are_never_written()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "downloads.xml");
        var store = new DownloadStore(path);
        var started = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        store.Save(new[]
        {
            new BrowserDownload("download-running", "https://example.com/running.bin", "running.bin",
                @"C:\Downloads\running.bin", BrowserDownloadState.InProgress, started, BrowserSessionKind.Normal),
            new BrowserDownload("download-private", "https://example.com/private.bin", "private.bin",
                @"C:\Downloads\private.bin", BrowserDownloadState.Completed, started, BrowserSessionKind.Private,
                started.AddMinutes(1), 10),
        });

        Assert.Empty(new DownloadStore(path).GetAll());
    }

    [Fact]
    public void Corrupt_downloads_fall_back_to_empty()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "downloads.xml");
        File.WriteAllText(path, "<downloads><broken>");

        Assert.Empty(new DownloadStore(path).GetAll());
    }

    [Fact]
    public void A_record_with_an_invalid_timestamp_is_dropped()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "downloads.xml");
        File.WriteAllText(path,
            "<downloads schemaVersion=\"1\"><entry>" +
            "<id>download-bad</id><url>https://example.com/file.bin</url>" +
            "<fileName>file.bin</fileName><targetPath>C:\\Downloads\\file.bin</targetPath>" +
            "<state>1</state><startedUtcTicks>-1</startedUtcTicks>" +
            "</entry></downloads>");

        Assert.Empty(new DownloadStore(path).GetAll());
    }

    [Fact]
    public void Entries_without_a_target_path_are_dropped()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "downloads.xml");
        File.WriteAllText(path,
            "<downloads schemaVersion=\"1\">" +
            "<entry><id>download-a</id><url>https://example.com/</url><fileName>a.bin</fileName>" +
            "<targetPath></targetPath><state>1</state><startedUtcTicks>638700000000000000</startedUtcTicks></entry>" +
            "<entry><id>download-b</id><url>https://example.com/</url><fileName>b.bin</fileName>" +
            "<targetPath>C:\\Downloads\\b.bin</targetPath><state>1</state><startedUtcTicks>638700000000000000</startedUtcTicks></entry>" +
            "</downloads>");

        var restored = new DownloadStore(path).GetAll();

        var entry = Assert.Single(restored);
        Assert.Equal("download-b", entry.Id);
    }

    [Fact]
    public void An_in_progress_entry_in_the_file_is_dropped()
    {
        // Only terminal records are ever written, so an in-progress entry is one a hand edit put
        // there; it is not a finished record and is not restored.
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "downloads.xml");
        File.WriteAllText(path,
            "<downloads schemaVersion=\"1\">" +
            "<entry><id>download-a</id><url>https://example.com/</url><fileName>a.bin</fileName>" +
            "<targetPath>C:\\Downloads\\a.bin</targetPath><state>0</state><startedUtcTicks>638700000000000000</startedUtcTicks></entry>" +
            "<entry><id>download-b</id><url>https://example.com/</url><fileName>b.bin</fileName>" +
            "<targetPath>C:\\Downloads\\b.bin</targetPath><state>1</state><startedUtcTicks>638700000000000000</startedUtcTicks></entry>" +
            "</downloads>");

        var restored = new DownloadStore(path).GetAll();

        var entry = Assert.Single(restored);
        Assert.Equal("download-b", entry.Id);
    }

    [Fact]
    public void A_paused_download_is_never_written()
    {
        // A paused download is still running, so it is not a finished record; the browser closes
        // without a trace of it on the device.
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "downloads.xml");
        var store = new DownloadStore(path);
        var started = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        store.Save(new[]
        {
            new BrowserDownload("download-paused", "https://example.com/paused.iso", "paused.iso",
                @"C:\Downloads\paused.iso", BrowserDownloadState.Paused, started, BrowserSessionKind.Normal,
                null, 2048, null, 4096),
            new BrowserDownload("download-done", "https://example.com/done.iso", "done.iso",
                @"C:\Downloads\done.iso", BrowserDownloadState.Completed, started.AddMinutes(1),
                BrowserSessionKind.Normal, started.AddMinutes(2), 99),
        });

        var restored = new DownloadStore(path).GetAll();

        var entry = Assert.Single(restored);
        Assert.Equal("download-done", entry.Id);
    }

    [Fact]
    public void A_persisted_record_keeps_the_bytes_it_had_when_it_was_written()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "downloads.xml");
        var store = new DownloadStore(path);
        var started = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        store.Save(new[]
        {
            new BrowserDownload("download-partial", "https://example.com/partial.iso", "partial.iso",
                @"C:\Downloads\partial.iso", BrowserDownloadState.Interrupted, started, BrowserSessionKind.Normal,
                started.AddMinutes(1), 2048, "NetworkError", 1024),
        });

        var restored = new DownloadStore(path).GetAll();

        var entry = Assert.Single(restored);
        Assert.Equal(1024UL, entry.ReceivedBytes);
    }

    [Fact]
    public void The_newest_download_is_listed_first()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "downloads.xml");
        var store = new DownloadStore(path);

        store.Save(new[]
        {
            Download("download-old", new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc)),
            Download("download-new", new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc)),
        });

        Assert.Equal("download-new", new DownloadStore(path).GetAll()[0].Id);
    }

    private static BrowserDownload Download(string id, DateTime started) => new(
        id, "https://example.com/file.bin", "file.bin", @"C:\Downloads\file.bin",
        BrowserDownloadState.Completed, started, BrowserSessionKind.Normal, started, 1);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(AppContext.BaseDirectory, ".test-data", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
