using Brezza.Core.Permissions;

namespace Brezza.Core.Tests.Permissions;

public sealed class PermissionNoticeLimiterTests
{
    private static readonly DateTime Start = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Repeated_requests_for_the_same_source_and_kind_are_throttled()
    {
        var limiter = new PermissionNoticeLimiter();

        Assert.True(limiter.ShouldNotify("https://example.com/", BrowserPermissionKind.Geolocation, Start));
        Assert.False(limiter.ShouldNotify("https://example.com/", BrowserPermissionKind.Geolocation, Start.AddSeconds(1)));
        Assert.True(limiter.ShouldNotify("https://example.com/", BrowserPermissionKind.Geolocation, Start.AddSeconds(31)));
    }

    [Fact]
    public void A_different_kind_is_not_throttled_by_another_kind()
    {
        var limiter = new PermissionNoticeLimiter();

        Assert.True(limiter.ShouldNotify("https://example.com/", BrowserPermissionKind.Geolocation, Start));
        Assert.True(limiter.ShouldNotify("https://example.com/", BrowserPermissionKind.Notifications, Start));
    }
}
