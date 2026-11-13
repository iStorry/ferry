using Ferry.Core.Download;
using Microsoft.Extensions.Time.Testing;

namespace Ferry.Core.Tests;

/// <summary>The token bucket on a fake clock: bursts, turns, and rate changes while connections wait.</summary>
public sealed class SpeedLimitTests : IDisposable
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);
    private readonly WatchedTime _time = new();

    public void Dispose() => _time.Dispose();

    private SpeedLimit Limit(long bytesPerSecond) => new(bytesPerSecond, _time);

    private void Advance(int milliseconds) => _time.Advance(TimeSpan.FromMilliseconds(milliseconds));

    [Fact]
    public void UnlimitedNeverWaits()
    {
        var limit = Limit(SpeedLimit.Unlimited);

        Assert.True(limit.WaitAsync(100 << 20, CancellationToken.None).AsTask().IsCompletedSuccessfully);
        Assert.Equal(1 << 20, limit.ChunkFor(1 << 20));
    }

    [Fact]
    public async Task BytesWithinTheBurstGoAtOnceAndTheRestWaitForTheRate()
    {
        var limit = Limit(1000); // a burst of 250 bytes

        Assert.True(limit.WaitAsync(250, CancellationToken.None).AsTask().IsCompletedSuccessfully);
        var next = limit.WaitAsync(500, CancellationToken.None).AsTask();
        Assert.False(next.IsCompleted);
        Advance(499);
        Assert.False(next.IsCompleted);
        Advance(1);
        await next.WaitAsync(Soon);
    }

    [Fact]
    public async Task WaitingConnectionsGoInTurn()
    {
        var limit = Limit(1000);
        await limit.WaitAsync(250, CancellationToken.None);
        var first = limit.WaitAsync(500, CancellationToken.None).AsTask();
        var second = limit.WaitAsync(500, CancellationToken.None).AsTask();

        Advance(500);
        await first.WaitAsync(Soon);
        Assert.False(second.IsCompleted);
        Advance(500);
        await second.WaitAsync(Soon);
    }

    [Fact]
    public async Task LiftingTheLimitLetsWaitingConnectionsGoAtOnce()
    {
        var limit = Limit(1000);
        await limit.WaitAsync(250, CancellationToken.None);
        var waiting = limit.WaitAsync(10_000, CancellationToken.None).AsTask();

        limit.BytesPerSecond = SpeedLimit.Unlimited;

        await waiting.WaitAsync(Soon);
        Assert.True(limit.WaitAsync(1 << 30, CancellationToken.None).AsTask().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RaisingTheLimitShortensAWaitAlreadyGoing()
    {
        var limit = Limit(1000);
        await limit.WaitAsync(250, CancellationToken.None);
        var waiting = limit.WaitAsync(1000, CancellationToken.None).AsTask(); // 1 s at 1000 B/s
        await _time.TimerStartedAsync();

        limit.BytesPerSecond = 2000;
        await _time.TimerStartedAsync();

        Advance(499);
        Assert.False(waiting.IsCompleted);
        Advance(1);
        await waiting.WaitAsync(Soon);
    }

    [Fact]
    public async Task LoweringTheLimitLengthensAWaitAlreadyGoing()
    {
        var limit = Limit(1000);
        await limit.WaitAsync(250, CancellationToken.None);
        var waiting = limit.WaitAsync(1000, CancellationToken.None).AsTask();
        await _time.TimerStartedAsync();

        limit.BytesPerSecond = 100; // the 1000 bytes owed now take 10 s
        await _time.TimerStartedAsync();

        Advance(1000);
        Assert.False(waiting.IsCompleted);
        Advance(9000);
        await waiting.WaitAsync(Soon);
    }

    [Fact]
    public async Task AWaitCanBeCancelled()
    {
        var limit = Limit(1000);
        await limit.WaitAsync(250, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = limit.WaitAsync(1000, cancel.Token).AsTask();

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Soon));
    }

    [Theory]
    [InlineData(80_000, 1 << 20, 16 << 10)]
    [InlineData(800_000, 1 << 20, 100_000)]
    [InlineData(80_000_000, 1 << 20, 1 << 20)]
    [InlineData(80_000, 1000, 1000)]
    public void ReadsAreAboutAnEighthOfASecond(long bytesPerSecond, int max, int expected) =>
        Assert.Equal(expected, Limit(bytesPerSecond).ChunkFor(max));

    [Fact]
    public void NegativeLimitsAreRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Limit(1000).BytesPerSecond = -1);
}

/// <summary>
/// A fake clock that says when a timer starts. A connection whose wait was cut short by a new rate starts its next
/// timer on another thread; moving the clock before that happens would start the timer from the later time.
/// </summary>
internal sealed class WatchedTime : FakeTimeProvider, IDisposable
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _started = new(0);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _started.Release();
        return timer;
    }

    public void Dispose() => _started.Dispose();

    /// <summary>Wait until the next timer, not waited for yet, has started.</summary>
    public async Task TimerStartedAsync()
    {
        if (!await _started.WaitAsync(Soon))
            throw new TimeoutException("No timer was started.");
    }
}
