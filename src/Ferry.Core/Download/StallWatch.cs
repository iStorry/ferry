namespace Ferry.Core.Download;

/// <summary>
/// Gives up on a connection that sends nothing for a while: its token is cancelled when a wait on the server runs past
/// the stall timeout. Armed before each wait on the server, stood down while waiting on anything else (the speed limit,
/// a new link, a place in line), which is no stall. Also cancelled when the try it belongs to is.
/// </summary>
internal sealed class StallWatch(TimeSpan timeout, Stage stage, CancellationToken tryCancel) : IDisposable
{
    private readonly CancellationTokenSource _source = CancellationTokenSource.CreateLinkedTokenSource(tryCancel);

    public CancellationToken Token => _source.Token;

    /// <summary>Wait on the server for <paramref name="wait"/>'s answer, as a stall if it takes too long.</summary>
    /// <exception cref="StageException">the server sent nothing within the timeout (retryable)</exception>
    public async Task<T> WaitAsync<T>(Func<CancellationToken, Task<T>> wait)
    {
        _source.CancelAfter(timeout);
        try
        {
            return await wait(Token);
        }
        catch (OperationCanceledException) when (!tryCancel.IsCancellationRequested)
        {
            throw Stalled();
        }
        finally
        {
            _source.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The next bytes of <paramref name="body"/> into <paramref name="buffer"/>, as a stall if none come in time.</summary>
    /// <exception cref="StageException">the server sent nothing within the timeout, or the connection dropped (both retryable)</exception>
    public async ValueTask<int> ReadAsync(Stream body, Memory<byte> buffer)
    {
        _source.CancelAfter(timeout);
        try
        {
            var read = await body.ReadAsync(buffer, Token);
            _source.CancelAfter(Timeout.InfiniteTimeSpan);
            return read;
        }
        catch (OperationCanceledException) when (!tryCancel.IsCancellationRequested)
        {
            throw Stalled();
        }
        catch (IOException e)
        {
            throw new StageException(stage, $"The connection dropped: {e.Message}", isRetryable: true, inner: e);
        }
    }

    public void Dispose() => _source.Dispose();

    private StageException Stalled() => new(stage, $"No data for {timeout.TotalSeconds:0.#} s.", isRetryable: true);
}
