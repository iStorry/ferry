using Ferry.Core.Http;

namespace Ferry.Core.Tests;

public sealed class RetryPolicyTests
{
    [Fact]
    public void TimeoutsStepUpToTheSlowTimeout()
    {
        var policy = new RetryPolicy(2, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(75));

        Assert.Equal([30, 52.5, 75, 75], [.. Enumerable.Range(1, 4).Select(a => policy.TimeoutFor(a).TotalSeconds)]);
    }

    [Fact]
    public void DelaysGrowWithJitterAndRetryAfterWins()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Assert.InRange(policy.DelayAfter(1).TotalSeconds, 2, 3);
        Assert.InRange(policy.DelayAfter(2).TotalSeconds, 4, 5);
        Assert.Equal(TimeSpan.FromSeconds(9), policy.DelayAfter(1, TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void RetryAfterIsFollowedUpToFiveMinutes()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromMinutes(5), policy.DelayAfter(1, TimeSpan.FromDays(100)));
        Assert.Equal(TimeSpan.Zero, policy.DelayAfter(1, TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void BusyServerGetsFiveTimesAsLong()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Assert.InRange(policy.DelayAfter(1, busy: true).TotalSeconds, 10, 11);
        Assert.InRange(policy.DelayAfter(2, busy: true).TotalSeconds, 20, 21);
        Assert.Equal(TimeSpan.FromSeconds(9), policy.DelayAfter(1, TimeSpan.FromSeconds(9), busy: true));
    }
}
