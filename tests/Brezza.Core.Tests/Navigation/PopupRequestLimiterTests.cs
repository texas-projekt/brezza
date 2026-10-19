using Brezza.Core.Navigation;

namespace Brezza.Core.Tests.Navigation;

public sealed class PopupRequestLimiterTests
{
    private static readonly DateTime Start = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_script_initiated_popup_is_never_allowed()
    {
        var limiter = new PopupRequestLimiter();

        Assert.False(limiter.TryAcquire("tab-1", userInitiated: false, Start));
    }

    [Fact]
    public void A_user_popup_is_allowed_once_per_tab_window()
    {
        var limiter = new PopupRequestLimiter();

        Assert.True(limiter.TryAcquire("tab-1", userInitiated: true, Start));
        Assert.False(limiter.TryAcquire("tab-1", userInitiated: true, Start.AddSeconds(1)));
        Assert.True(limiter.TryAcquire("tab-1", userInitiated: true, Start.AddSeconds(2)));
    }

    [Fact]
    public void The_global_window_allows_at_most_five_popups()
    {
        var limiter = new PopupRequestLimiter();

        for (var index = 0; index < 5; index++)
        {
            Assert.True(limiter.TryAcquire("tab-" + index, userInitiated: true, Start.AddSeconds(index)));
        }

        Assert.False(limiter.TryAcquire("tab-6", userInitiated: true, Start.AddSeconds(6)));
        Assert.True(limiter.TryAcquire("tab-6", userInitiated: true, Start.AddMinutes(2)));
    }
}
