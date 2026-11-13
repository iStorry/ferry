namespace Ferry.Core.Download;

/// <summary>
/// A speed limit in bytes per second, shared by every connection that uses it: one for all downloads, or one per
/// file. A token bucket: up to a quarter of a second's worth of bytes can go at once, then each connection waits its
/// turn. The rate can change at any time, and connections already waiting pick up the new rate at once.
/// </summary>
public sealed class SpeedLimit
{
    public const long Unlimited = 0;

    // reads of at least this much, so a low limit still uses the connection well
    private const int MinChunkBytes = 16 << 10;

    // reads of about an eighth of a second's worth, so the bytes flow evenly instead of in bursts
    private const int ChunksPerSecond = 8;

    private static readonly TimeSpan Burst = TimeSpan.FromMilliseconds(250);
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private long _rate;
    private long _lastRefill;

    // below zero when connections took bytes ahead of the rate; they wait until it is paid back
    private double _tokens;

    // every token ever added; a waiting connection may go once this reaches its mark
    private double _refilled;

    private TaskCompletionSource _rateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="time">the clock, replaceable in tests</param>
    public SpeedLimit(long bytesPerSecond = Unlimited, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastRefill = _time.GetTimestamp();
        BytesPerSecond = bytesPerSecond;
    }

    /// <summary>The limit, or <see cref="Unlimited"/>.</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_lock)
                return _rate;
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            TaskCompletionSource changed;
            lock (_lock)
            {
                Refill();
                if (value == Unlimited)
                {
                    // whatever is owed is forgiven: everyone waiting may go
                    _refilled += Math.Max(0, -_tokens);
                    _tokens = 0;
                }
                else
                {
                    _tokens = _rate == Unlimited ? Capacity(value) : Math.Min(_tokens, Capacity(value));
                }
                _rate = value;
                (changed, _rateChanged) = (_rateChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            changed.TrySetResult();
        }
    }

    /// <summary>How many bytes to read at a time under this limit, at most <paramref name="max"/>.</summary>
    public int ChunkFor(int max)
    {
        var rate = BytesPerSecond;
        return rate == Unlimited ? max : (int)Math.Clamp(rate / ChunksPerSecond, Math.Min(MinChunkBytes, max), max);
    }

    /// <summary>Count <paramref name="bytes"/> just received, and wait until the limit allows them.</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken cancel)
    {
        double mark;
        lock (_lock)
        {
            if (_rate == Unlimited)
                return;
            Refill();
            _tokens -= bytes;
            if (_tokens >= 0)
                return;
            mark = _refilled - _tokens;
        }
        while (true)
        {
            TimeSpan wait;
            Task rateChanged;
            lock (_lock)
            {
                Refill();
                if (_rate == Unlimited || _refilled >= mark)
                    return;
                wait = TimeSpan.FromSeconds((mark - _refilled) / _rate);
                rateChanged = _rateChanged.Task;
            }
            using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            await Task.WhenAny(Task.Delay(wait, _time, delayCancel.Token), rateChanged);
            await delayCancel.CancelAsync(); // a changed rate ended the wait early: stop its timer
            cancel.ThrowIfCancellationRequested();
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var added = _rate * _time.GetElapsedTime(_lastRefill, now).TotalSeconds;
        _lastRefill = now;
        if (_rate == Unlimited)
            return;
        _refilled += added;
        _tokens = Math.Min(Capacity(_rate), _tokens + added);
    }

    private static double Capacity(long rate) => Math.Max(1, rate * Burst.TotalSeconds);
}
