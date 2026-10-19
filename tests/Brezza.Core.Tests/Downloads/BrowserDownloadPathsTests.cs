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
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.log", "_COM1.log")]
    public void A_suggested_name_cannot_escape_the_downloads_folder(string? suggested, string expected)
    {
        Assert.Equal(expected, BrowserDownloadPaths.SanitizeFileName(suggested));
    }

    [Fact]
    public void A_very_long_name_is_truncated_but_keeps_its_extension()
    {
        var sanitized = BrowserDownloadPaths.SanitizeFileName(new string('a', 400) + ".pdf");

        Assert.True(sanitized.Length <= BrowserDownloadPaths.MaxFileNameLength);
        Assert.EndsWith(".pdf", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void A_numbered_candidate_also_stays_within_the_limit()
    {
        var name = new string('b', BrowserDownloadPaths.MaxFileNameLength - 4) + ".txt";
        File.WriteAllText(Path.Combine(_folder, name), "existing");

        var target = BrowserDownloadPaths.ResolveTargetPath(_folder, name, File.Exists);

        Assert.True(Path.GetFileName(target).Length <= BrowserDownloadPaths.MaxFileNameLength);
        Assert.EndsWith(".txt", target, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_url_escape_is_kept_as_a_safe_file_name()
    {
        Assert.Equal("%zz", BrowserDownloadPaths.SuggestedFileNameFrom(null, "https://example.com/%zz"));
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
