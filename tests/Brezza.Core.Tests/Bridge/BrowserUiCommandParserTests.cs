using Brezza.Core.Bridge;

namespace Brezza.Core.Tests.Bridge;

public sealed class BrowserUiCommandParserTests
{
    [Fact]
    public void Accepts_allowlisted_navigation_command_with_current_version()
    {
        var message = new Dictionary<string, object>
        {
            ["version"] = BrowserUiCommandParser.CurrentProtocolVersion,
            ["type"] = "navigate",
            ["args"] = new Dictionary<string, object> { ["text"] = "example.com" },
        };

        Assert.True(BrowserUiCommandParser.TryParse(message, out var command, out _));
        Assert.Equal("example.com", command!.GetString("text"));
    }

    [Theory]
    [InlineData(2, "navigate", "example.com")]
    [InlineData(1, "executeScript", "alert(1)")]
    [InlineData(1, "navigate", "")]
    public void Rejects_unknown_versions_commands_and_invalid_required_arguments(int version, string type, string text)
    {
        var message = new Dictionary<string, object>
        {
            ["version"] = version,
            ["type"] = type,
            ["args"] = new Dictionary<string, object> { ["text"] = text },
        };

        Assert.False(BrowserUiCommandParser.TryParse(message, out _, out _));
    }

    [Fact]
    public void Rejects_oversized_navigation_payloads()
    {
        var message = new Dictionary<string, object>
        {
            ["version"] = 1,
            ["type"] = "navigate",
            ["args"] = new Dictionary<string, object> { ["text"] = new string('a', 8193) },
        };

        Assert.False(BrowserUiCommandParser.TryParse(message, out _, out _));
    }

    [Fact]
    public void Accepts_chrome_layout_within_bounds()
    {
        var message = new Dictionary<string, object>
        {
            ["version"] = 1,
            ["type"] = "layout",
            ["args"] = new Dictionary<string, object> { ["height"] = 88 },
        };

        Assert.True(BrowserUiCommandParser.TryParse(message, out var command, out _));
        Assert.Equal(88, command!.GetInt32("height"));
        Assert.True(BrowserUiCommandParser.IsChromeSurfaceOnly(command.Type));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(BrowserUiCommandParser.MaximumChromeHeight + 1)]
    public void Rejects_out_of_range_layout(int height)
    {
        var message = new Dictionary<string, object>
        {
            ["version"] = 1,
            ["type"] = "layout",
            ["args"] = new Dictionary<string, object> { ["height"] = height },
        };

        Assert.False(BrowserUiCommandParser.TryParse(message, out _, out _));
    }

    [Fact]
    public void Remove_bookmark_requires_a_url_and_is_allowed_from_internal_pages()
    {
        var valid = new Dictionary<string, object>
        {
            ["version"] = 1,
            ["type"] = "removeBookmark",
            ["args"] = new Dictionary<string, object> { ["url"] = "https://example.com/" },
        };
        var missing = new Dictionary<string, object>
        {
            ["version"] = 1,
            ["type"] = "removeBookmark",
            ["args"] = new Dictionary<string, object>(),
        };

        Assert.True(BrowserUiCommandParser.TryParse(valid, out var command, out _));
        Assert.False(BrowserUiCommandParser.IsChromeSurfaceOnly(command!.Type));
        Assert.False(BrowserUiCommandParser.TryParse(missing, out _, out _));
    }
}
