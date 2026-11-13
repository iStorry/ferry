using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Microsoft.Win32.SafeHandles;

namespace Ferry.Core.Download;

/// <summary>
/// Writes a file into its part as connections bring its segments, wherever they bring them from: a web server's
/// ranges, or the agent on a server over ssh. One handle, written at each segment's offset, made sparse so that the
/// first write far into the file doesn't fill everything before it with zeros. While the connections run it saves
/// which bytes are done every few seconds (so a crash loses little), reports progress, and when asked hashes the file
/// as its start fills in, so the SHA-256 is known the moment the last byte lands rather than after reading the whole
/// file again.
/// </summary>
/// <param name="target">the file's final path; the part and its state are named after it</param>
/// <param name="state">what to save with each snapshot of the plan, apart from the segments</param>
/// <param name="stage">whose failures these are: a download's or a transfer's</param>
/// <param name="hash">whether anything needs the file's SHA-256</param>
internal sealed class PartWriter(string target, SegmentPlan plan, PartState state, Throttle throttle, Stage stage,
                                 TimeSpan stallTimeout, Action<DownloadEvent> onEvent, bool hash)
{
    private const int BufferSize = 1 << 20;
    private SafeFileHandle? _handle;

    /// <summary>Where a segment's bytes come from: a stream over [<paramref name="from"/>, <paramref name="end"/>) of the file.</summary>
    /// <param name="watch">what to wait on the server through; other waits, on its token, are no stall</param>
    public delegate Task<Stream> Source(long from, long end, StallWatch watch);

    public SegmentPlan Plan => plan;

    /// <summary>
    /// Open the part, run <paramref name="work"/> over it (the connections, through <see cref="RunConnectionsAsync"/>),
    /// and return the file's SHA-256 (lower-case hex) once every segment is done, null when it isn't to be hashed.
    /// Stopping early, for whatever reason, leaves the part and its state for a later resume.
    /// </summary>
    /// <param name="connections">how many connections <paramref name="work"/> will run, for the events</param>
    /// <exception cref="IOException">the part can't be written</exception>
    public async Task<string?> RunAsync(int connections, Func<CancellationToken, Task> work, CancellationToken cancel)
    {
        var (part, total, resumed) = (PartFile.PartPath(target), state.Total, plan.Done > 0);
        using var handle = Open(part, total, resumed);
        _handle = handle;
        void Save()
        {
            var segments = plan.Snapshot();
            RandomAccess.FlushToDisk(handle); // everything in the snapshot is on disk before the state says so
            PartFile.Save(target, state with { Segments = segments });
        }

        Save();
        onEvent(new DownloadStarted(target, total, plan.Done, connections, resumed));
        var reporter = new ProgressReporter(() => plan.Done, total, () => plan.Active, onEvent, Save);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var hashing = hash ? HashAsync(handle, total, stop.Token) : Task.FromResult<string?>(null);
            var working = WorkAsync(work, stop.Token);
            try
            {
                await await Task.WhenAny(working, hashing);
                await working;
                return await hashing;
            }
            catch
            {
                await stop.CancelAsync();
                await EndedAsync(working, hashing);
                throw;
            }
        }
        finally
        {
            _handle = null;
            await reporter.DisposeAsync();
            if (!plan.IsComplete)
            {
                try
                {
                    Save();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the last state saved still holds, and a pause or a failure is what should be reported
                }
            }
        }
    }

    /// <summary>
    /// Run <paramref name="count"/> connections, each taking segments until none is left and trying
    /// <paramref name="attempt"/> on its segment under <paramref name="retry"/>; the first to fail for good stops the
    /// others. A failure <paramref name="leaveOn"/> says yes to ends the connection instead, its segment left to the
    /// others.
    /// </summary>
    public async Task RunConnectionsAsync(int count, RetryPolicy retry, Func<Segment, CancellationToken, Task> attempt,
                                          CancellationToken cancel, Func<StageException, bool>? leaveOn = null)
    {
        using var stopAll = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var connections = Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (plan.Take() is { } segment)
                {
                    try
                    {
                        var kept = await Retry.RunAsync(stage, retry, async tryCancel =>
                        {
                            try
                            {
                                await attempt(segment, tryCancel);
                                return true;
                            }
                            catch (StageException e) when (leaveOn?.Invoke(e) == true)
                            {
                                return false;
                            }
                        }, stopAll.Token, DownloadRetrying.Reporter(onEvent), () => plan.DoneOf(segment));
                        if (!kept)
                            break;
                    }
                    finally
                    {
                        plan.Release(segment);
                    }
                }
            }
            catch
            {
                await stopAll.CancelAsync();
                throw;
            }
        }, stopAll.Token)).ToList();
        try
        {
            await Task.WhenAll(connections);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            // stopped because another connection failed: that failure is the one to report
            var failure = connections.Select(c => c.Exception?.InnerException).OfType<Exception>()
                                     .First(e => e is not OperationCanceledException);
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>
    /// Bring what is left of <paramref name="segment"/> from <paramref name="source"/> into the part, or until another
    /// connection takes over its end. Only to be called from the work given to <see cref="RunAsync"/>.
    /// </summary>
    /// <exception cref="StageException">the server stalled, the connection dropped or closed early (all retryable)</exception>
    public async Task FetchAsync(Segment segment, Source source, CancellationToken tryCancel)
    {
        var handle = _handle ?? throw new InvalidOperationException("The part is not open.");
        plan.Rewind(segment);
        var (from, end) = plan.Next(segment);
        if (from >= end)
            return;
        using var watch = new StallWatch(stallTimeout, stage, tryCancel);
        await using var body = await source(from, end, watch);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            plan.Receiving(segment, true);
            var offset = from;
            while (await watch.ReadAsync(body, buffer.AsMemory(0, throttle.ChunkFor(BufferSize))) is var read and > 0)
            {
                var allowed = plan.Claim(segment, read);
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, allowed), offset, tryCancel);
                plan.Written(segment, allowed);
                offset += allowed;
                await throttle.WaitAsync(read, tryCancel);
                if (allowed < read || plan.Next(segment) is var (next, stop) && next >= stop)
                    return; // done, or the rest went to another connection
            }
        }
        finally
        {
            plan.Receiving(segment, false);
            ArrayPool<byte>.Shared.Return(buffer);
        }
        throw new StageException(stage, "The connection closed before its part was complete.", isRetryable: true);
    }

    /// <summary>The part, the size of the whole file, sparse where the file system allows.</summary>
    private static SafeFileHandle Open(string part, long total, bool resumed)
    {
        // the sparse request isn't an overlapped one, so it goes on a handle that isn't either
        using (var plain = File.OpenHandle(part, resumed ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            SparseFile.TryMark(plain);
            RandomAccess.SetLength(plain, total);
        }
        return File.OpenHandle(part, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
    }

    private async Task WorkAsync(Func<CancellationToken, Task> work, CancellationToken cancel)
    {
        await work(cancel);
        if (!plan.IsComplete)
            throw new UnreachableException("Every connection finished but parts of the file are missing.");
    }

    /// <summary>
    /// Hash the file from its start as far as the bytes reach without a gap, catching up whenever they reach further:
    /// the bytes just written are still in the OS cache, so this costs no disk read. On a resume the start already
    /// there is hashed first. Done when the whole file is.
    /// </summary>
    private async Task<string?> HashAsync(SafeFileHandle handle, long total, CancellationToken cancel)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var hashed = 0L;
        while (hashed < total)
        {
            var reach = plan.ContiguousDone;
            if (reach <= hashed)
            {
                await plan.ContiguousGrewAsync(hashed).WaitAsync(cancel);
                continue;
            }
            await FileChecksum.AppendAsync(sha256, handle, hashed, reach, cancel);
            hashed = reach;
        }
        return Convert.ToHexStringLower(sha256.GetHashAndReset());
    }

    /// <summary>Wait for the tasks to end, however they end: the failure already caught is the one to report, and the handle must stay open until they have stopped.</summary>
    private static async Task EndedAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            // cancelled on purpose, or failed after the failure being reported
        }
    }
}
