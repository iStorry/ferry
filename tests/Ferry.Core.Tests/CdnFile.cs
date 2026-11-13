using System.Collections.Concurrent;
using System.Net;

namespace Ferry.Core.Tests;

/// <summary>
/// A file on <see cref="FakeSite"/>'s CDN: ranges, ETag, and failures at a byte offset (drop the connection, stall,
/// expire the tokens, hold until released), each happening once.
/// </summary>
internal sealed class CdnFile(byte[] data)
{
    private const int ChunkBytes = 64 << 10;

    // longer than any test's stall timeout; the connection is dropped afterwards
    private static readonly TimeSpan StallFor = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _served;
    private int _dropped, _stalled, _expired, _active;

    public byte[] Data { get; set; } = data;
    public string? ETag { get; set; } = "\"v1\"";
    public string ContentType { get; set; } = "video/mp4";

    /// <summary>Answer every request with the whole file (200), as a server without range support does.</summary>
    public bool IgnoreRange { get; set; }

    /// <summary>Answer the first-byte probe with a part (206) but every other range with the whole file (200).</summary>
    public bool RangesForProbeOnly { get; set; }

    /// <summary>Answer 416 to the first-byte probe, whatever the file; other requests are answered as ever.</summary>
    public bool RefuseFirstByte { get; set; }

    /// <summary>Bytes from this offset are sent only after <see cref="Release"/>.</summary>
    public long? HoldAt { get; set; }

    public long? DropOnceAt { get; set; }
    public long? StallOnceAt { get; set; }

    /// <summary>When a response reaches this offset, every token issued so far stops working and the connection drops.</summary>
    public long? ExpireTokensAt { get; set; }

    /// <summary>Answer 503 to a request while this many are already being answered, as a CDN limiting connections does.</summary>
    public int? BusyAbove { get; set; }

    /// <summary>A pause after every 64 KB, for a slow server.</summary>
    public TimeSpan ChunkDelay { get; set; }

    /// <summary>Set when a request first reaches <see cref="HoldAt"/>.</summary>
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long BytesServed => Interlocked.Read(ref _served);

    /// <summary>Each request's Range header (null without one), in order.</summary>
    public ConcurrentQueue<string?> Ranges { get; } = new();

    public void Release() => _released.TrySetResult();

    /// <summary>Answer <paramref name="context"/>, closing its response exactly once (HttpListener applies a second
    /// close of a kept-alive connection's response to the next request on it).</summary>
    /// <param name="sent">called once the last byte is sent (or sending stopped), before the response is closed: by
    /// then the downloader may already be making its next request</param>
    public async Task ServeAsync(HttpListenerContext context, bool tokenValid, Action revokeTokens, Action? sent = null)
    {
        var finished = 0;
        void Sent()
        {
            if (Interlocked.Exchange(ref finished, 1) == 0)
                sent?.Invoke();
        }

        var response = context.Response;
        var header = context.Request.Headers["Range"];
        Ranges.Enqueue(header);
        var active = Interlocked.Increment(ref _active);
        try
        {
            if (BusyAbove is { } most && active > most)
            {
                Sent();
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                response.Close();
                return;
            }
            if (!tokenValid)
            {
                Sent();
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                response.Close();
                return;
            }
            var data = Data;
            if (Parse(header, data.Length) is not var (from, to, partial))
            {
                // a range past the end: for an empty file, every range is
                Sent();
                response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                response.AddHeader("Content-Range", $"bytes */{data.Length}");
                response.Close();
                return;
            }
            response.StatusCode = (int)(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
            response.ContentType = ContentType;
            response.AddHeader("Accept-Ranges", "bytes");
            if (ETag is not null)
                response.AddHeader("ETag", ETag);
            if (partial)
                response.AddHeader("Content-Range", $"bytes {from}-{to}/{data.Length}");
            response.ContentLength64 = to - from + 1;

            for (var position = from; position <= to;)
            {
                if (HoldAt is { } hold && position >= hold && !_released.Task.IsCompleted)
                {
                    Held.TrySetResult();
                    await _released.Task;
                }
                if (Fires(DropOnceAt, position, ref _dropped))
                {
                    response.Abort();
                    return;
                }
                if (Fires(StallOnceAt, position, ref _stalled))
                {
                    await Task.Delay(StallFor);
                    response.Abort();
                    return;
                }
                if (Fires(ExpireTokensAt, position, ref _expired))
                {
                    revokeTokens();
                    response.Abort();
                    return;
                }
                var end = new[] { to + 1, position + ChunkBytes, After(position, DropOnceAt, _dropped),
                                  After(position, StallOnceAt, _stalled), After(position, ExpireTokensAt, _expired) }.Min();
                // over before its last bytes go, not after the write returns: by then the downloader may have read them, let its
                // connection go and asked for the next part, and that request mustn't count on top of this one
                if (end > to)
                    Sent();
                await response.OutputStream.WriteAsync(data.AsMemory((int)position, (int)(end - position)));
                Interlocked.Add(ref _served, end - position);
                position = end;
                if (ChunkDelay > TimeSpan.Zero && position <= to)
                    await Task.Delay(ChunkDelay);
            }
            Sent();
            response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
            // the downloader closed the connection (cancelled, or another connection took over the rest)
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Sent();
        }
    }

    /// <summary>What to send for <paramref name="header"/>; null when the range asked for lies past the end.</summary>
    private (long From, long To, bool Partial)? Parse(string? header, long length)
    {
        if (RefuseFirstByte && header == "bytes=0-0")
            return null;
        if (IgnoreRange || header is null || !header.StartsWith("bytes=", StringComparison.Ordinal) || (RangesForProbeOnly && header != "bytes=0-0"))
            return (0, length - 1, false);
        var parts = header["bytes=".Length..].Split('-');
        var from = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var to = parts[1].Length == 0 ? length - 1 : Math.Min(length - 1, long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        return from > length - 1 ? null : (from, to, true);
    }

    private static bool Fires(long? offset, long position, ref int fired) =>
        offset is { } at && position >= at && Interlocked.Exchange(ref fired, 1) == 0;

    /// <summary>A chunk ends at a failure's offset that is still to come, so the failure happens exactly there.</summary>
    private static long After(long position, long? offset, int fired) =>
        offset is { } at && at > position && fired == 0 ? at : long.MaxValue;
}
