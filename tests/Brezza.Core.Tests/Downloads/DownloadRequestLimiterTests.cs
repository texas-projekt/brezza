using Brezza.Core.Downloads;

namespace Brezza.Core.Tests.Downloads;

public sealed class DownloadRequestLimiterTests
{
    [Fact]
    public void A_download_with_a_known_oversized_total_is_refused_before_a_slot_is_used()
    {
        var limiter = new DownloadRequestLimiter();

        var decision = limiter.TryAcquire("C:\\Downloads\\large.iso", DownloadRequestLimiter.MaximumBytes + 1);

        Assert.False(decision.IsAllowed);
        Assert.Equal(DownloadBlockReason.TooLarge, decision.Reason);
        Assert.Equal(0, limiter.ActiveCount);
    }

    [Fact]
    public void Three_live_downloads_exhaust_the_shared_quota()
    {
        var limiter = new DownloadRequestLimiter();

        Assert.True(limiter.TryAcquire("C:\\Downloads\\a", null).IsAllowed);
        Assert.True(limiter.TryAcquire("C:\\Downloads\\b", 1).IsAllowed);
        Assert.True(limiter.TryAcquire("C:\\Downloads\\c", 2).IsAllowed);

        var decision = limiter.TryAcquire("C:\\Downloads\\d", null);

        Assert.False(decision.IsAllowed);
        Assert.Equal(DownloadBlockReason.TooManyConcurrent, decision.Reason);
        Assert.Equal(3, limiter.ActiveCount);
    }

    [Fact]
    public void Releasing_a_finished_download_makes_room_for_the_next_one()
    {
        var limiter = new DownloadRequestLimiter();
        for (var index = 0; index < 3; index++)
        {
            Assert.True(limiter.TryAcquire("C:\\Downloads\\" + index, null).IsAllowed);
        }

        limiter.Release("C:\\Downloads\\1");

        Assert.Equal(2, limiter.ActiveCount);
        Assert.True(limiter.TryAcquire("C:\\Downloads\\next", null).IsAllowed);
    }

    [Fact]
    public void A_paused_download_keeps_its_slot_until_it_is_released()
    {
        var limiter = new DownloadRequestLimiter();
        Assert.True(limiter.TryAcquire("C:\\Downloads\\paused", null).IsAllowed);

        // Pausing does not release; the operation and its partial bytes still exist.
        Assert.Equal(1, limiter.ActiveCount);

        limiter.Release("C:\\Downloads\\paused");
        Assert.Equal(0, limiter.ActiveCount);
    }
}
