using Brezza.Core.Downloads;

namespace Brezza.Core.Tests.Downloads;

public sealed class BrowserDownloadPathsTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, ".test-data", Guid.NewGuid().ToString("N"));

    public BrowserDownloadPathsTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void A_free_file_name_is_used_as_is()
    {
        Assert.Equal(Path.Combine(_folder, "report.pdf"), BrowserDownloadPaths.ResolveTargetPath(_folder, "report.pdf", File.Exists));
    }

    [Fact]
    public void An_existing_file_gets_a_numbered_suffix()
    {
        File.WriteAllText(Path.Combine(_folder, "report.pdf"), "existing");

        var first = BrowserDownloadPaths.ResolveTargetPath(_folder, "report.pdf", File.Exists);
        File.WriteAllText(first, "first");

        Assert.Equal(Path.Combine(_folder, "report (1).pdf"), first);
        Assert.Equal(Path.Combine(_folder, "report (2).pdf"), BrowserDownloadPaths.ResolveTargetPath(_folder, "report.pdf", File.Exists));
    }

    [Theory]
    [InlineData("../../secret.txt", "secret.txt")]
    [InlineData("..", "download")]
    [InlineData("", "download")]
    [InlineData("   ", "download")]
    [InlineData(null, "download")]
    [InlineData("bad|name?.txt", "badname.txt")]
    public void A_suggested_name_cannot_escape_the_downloads_folder(string? suggested, string expected)
    {
        Assert.Equal(expected, BrowserDownloadPaths.SanitizeFileName(suggested));
    }

    [Fact]
    public void The_disposition_file_name_wins_over_the_url_segment()
    {
        var name = BrowserDownloadPaths.SuggestedFileNameFrom(
            "attachment; filename=\"Invoice 2026.pdf\"",
            "https://example.com/download?id=7");

        Assert.Equal("Invoice 2026.pdf", name);
    }

    [Fact]
    public void The_url_segment_is_used_when_there_is_no_disposition()
    {
        var name = BrowserDownloadPaths.SuggestedFileNameFrom(null, "https://example.com/files/Setup.exe");

        Assert.Equal("Setup.exe", name);
    }

    [Fact]
    public void A_query_only_url_falls_back_to_the_default_name()
    {
        Assert.Equal("download", BrowserDownloadPaths.SuggestedFileNameFrom(null, "https://example.com/download?id=7"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }
}
