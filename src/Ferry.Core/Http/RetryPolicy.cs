namespace Ferry.Core.Http;

/// <summary>
/// How often to try a step and how long each try may take. The first try gets <see cref="Timeout"/>; each retry gets
/// a little longer, up to <see cref="SlowTimeout"/> on the last, for pages that are slow rather than down.
/// </summary>
/// <param name="Wait">the pause before the first retry; it grows with each retry, plus up to half of it at random</param>
/// <param name="Timeout"><see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for no limit per try: downloads,
/// which watch for stalls instead</param>
public sealed record RetryPolicy(int MaxRetries, TimeSpan Wait, TimeSpan Timeout, TimeSpan SlowTimeout)
{
    private const int BusyFactor = 5;

    // a server's Retry-After is followed up to this long; beyond it the try comes sooner than the server asked
    private static readonly TimeSpan LongestRetryAfter = TimeSpan.FromMinutes(5);

    public int MaxAttempts => Math.Max(0, MaxRetries) + 1;

    /// <summary>Tries without a time limit, for downloads.</summary>
    public static RetryPolicy Untimed(int maxRetries, TimeSpan wait) =>
        new(maxRetries, wait, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);

    /// <param name="attempt">1 for the first try</param>
    public TimeSpan TimeoutFor(int attempt)
    {
        if (Timeout == System.Threading.Timeout.InfiniteTimeSpan || attempt <= 1 || MaxRetries <= 0 || SlowTimeout <= Timeout)
            return Timeout;
        var step = (SlowTimeout - Timeout) / MaxRetries;
        var timeout = Timeout + step * (attempt - 1);
        return timeout < SlowTimeout ? timeout : SlowTimeout;
    }

    /// <param name="attempt">the try that just failed, 1 for the first</param>
    /// <param name="busy">the server said it is overloaded (503, 429): it gets five times as long to recover</param>
    public TimeSpan DelayAfter(int attempt, TimeSpan? retryAfter = null, bool busy = false)
    {
        if (retryAfter is { } asked)
            return asked < TimeSpan.Zero ? TimeSpan.Zero : asked > LongestRetryAfter ? LongestRetryAfter : asked;
        var backoff = Wait * attempt * (busy ? BusyFactor : 1);
        var jitter = Wait / 2 * Random.Shared.NextDouble();
        return backoff + jitter;
    }
}
