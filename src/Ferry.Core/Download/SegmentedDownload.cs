using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Ferry.Core.Sites;
using Ferry.Core.Files;
using Ferry.Core.Http;

namespace Ferry.Core.Download;

/// <summary>
/// Downloads a file over several connections at once, each fetching its own byte range into the same part
/// file. It continues where it stopped (after a cancel, a crash or a restart), fetches a new link when the old one
/// expires, retries a connection that drops or stalls, and gives the file its final name only once it is complete.
/// A server that ignores ranges gets one connection, and then an interrupted download starts over.
/// </summary>
/// <param name="overallLimit">a speed limit shared by every download this instance runs</param>
/// <param name="hosts">a cap on connections per server, shared by every download this instance runs</param>
public sealed class SegmentedDownload(HttpFetcher http, SiteCatalog sites, DownloadOptions options,
                                      SpeedLimit? overallLimit = null, HostConnections? hosts = null)
{
    private const int BufferSize = 1 << 20;

    /// <summary>Download <paramref name="request"/>; cancelling keeps what was downloaded for next time.</summary>
    /// <param name="onEvent">progress and what happened, on a background thread</param>
    /// <exception cref="StageException">the file could not be resolved or downloaded</exception>
    /// <exception cref="IOException">the file could not be written</exception>
    public async Task<DownloadResult> RunAsync(DownloadRequest request, Action<DownloadEvent> onEvent, CancellationToken cancel)
    {
        var details = request.Details ?? RequestDetails.None;
        var first = await sites.ResolveAsync(request.Link, details, cancel);
        var name = request.Name is { } custom ? FileNames.Sanitize(custom) : first.Name;
        var folder = FolderFor(request, name);
        if (options.Place is { } place && !request.FolderChosen && request.Name is null)
            (folder, name) = place(folder, name);
        Directory.CreateDirectory(folder);
        var target = DownloadTarget.Choose(Path.Combine(folder, name), request.Link.Key, options.OnExisting);
        if (target.Skip)
        {
            // what is there has to be the file that was asked for
            var existing = await VerifiedAsync(new DownloadResult(target.Path, Skipped: true), request);
            onEvent(new DownloadSkipped(existing.Path));
            return existing;
        }
        try
        {
            if (target.Saved is not null && first.IsWebPage)
                throw new StageException(Stage.Download, SignedFile.NowAWebPage);
            using var signed = new SignedFile(sites, first, details, onEvent);
            var job = new Job(target, signed, new Throttle(overallLimit, request.Limit), onEvent, Hash: request.Sha256 is not null || options.AlwaysHash);
            try
            {
                return await VerifiedAsync(await TransferAsync(job, cancel), request);
            }
            catch (FileChangedException e)
            {
                PartFile.Delete(target.Path);
                onEvent(new DownloadRestarted(e.Message));
                try
                {
                    return await VerifiedAsync(await TransferAsync(job with { Target = target with { Saved = null }, AskAgain = true }, cancel), request);
                }
                catch (FileChangedException again)
                {
                    throw new StageException(Stage.Download, $"{again.Message} Try again later.", inner: again);
                }
            }
        }
        finally
        {
            DownloadTarget.Release(target.Path);
        }
    }

    private string FolderFor(DownloadRequest request, string name) =>
        options.SortByType && !request.FolderChosen ? FileKinds.SortedInto(request.Folder, name) : request.Folder;

    /// <summary>
    /// The result, if its file is what it should be: by the hash made as it arrived, or by reading it when there is
    /// none. Not cancelled by a pause, which it would have nothing to go back to: the part is already gone. A file
    /// that doesn't match stays, for the user to look at.
    /// </summary>
    /// <exception cref="StageException">it doesn't match the checksum</exception>
    private static async Task<DownloadResult> VerifiedAsync(DownloadResult result, DownloadRequest request)
    {
        if (request.Sha256 is not { } sha256)
            return result;
        var path = result.Path;
        if (Directory.Exists(path))
            throw new StageException(Stage.Verify, $"{Path.GetFileName(path)} is a folder of files, so a SHA-256 can't be checked against it. It was kept.");
        var matches = result.Sha256 is { } hashed ? hashed == sha256 : await FileChecksum.MatchesAsync(path, sha256);
        return matches
            ? result
            : throw new StageException(Stage.Verify, $"{Path.GetFileName(path)} doesn't match the SHA-256 it should have. It is damaged or a different file; it was kept.");
    }

    /// <summary>Download the file into its part, then give it its final name.</summary>
    private async Task<DownloadResult> TransferAsync(Job job, CancellationToken cancel)
    {
        if (job.Signed.Current.Transport is { } transport)
            return await ThroughTransportAsync(job, transport, cancel);
        var (target, onEvent) = (job.Target, job.OnEvent);
        var probe = await ProbeAsync(job, cancel);
        var saved = target.Saved;
        // a server's passing mistake must neither cost what is on disk nor pass for the finished file
        if (probe.Total == 0 && (saved is { Total: > 0 } || job.Signed.Current.Size is > 0))
            throw new StageException(Stage.Download, "The server now says the file is empty. What was downloaded is kept.", isRetryable: true);
        if (saved is not null && !probe.Continues(saved))
        {
            PartFile.Delete(target.Path);
            onEvent(new DownloadRestarted("The file changed on the server since the download stopped."));
            saved = null;
        }
        (long Bytes, string? Sha256) done;
        // what continues was sent in parts before, whatever the first byte's answer said: asking for a part is what tells
        if ((probe.Ranges || saved is not null) && probe.Total is { } total)
        {
            try
            {
                done = await SegmentsAsync(job, probe, total, saved, cancel);
            }
            catch (RangesIgnoredException)
            {
                // it said it would send parts, then sent the whole file to a request for one: as aria2 and IDM do,
                // take it the way it gives it
                PartFile.Delete(target.Path);
                onEvent(new DownloadRestarted("The server stopped sending the file in parts; downloading it on one connection instead."));
                done = await StreamAsync(job, probe with { Ranges = false }, cancel);
            }
        }
        else
        {
            done = await StreamAsync(job, probe, cancel);
        }
        var final = target.Complete();
        onEvent(new DownloadFinished(final, done.Bytes));
        return new DownloadResult(final, Skipped: false, done.Sha256);
    }

    /// <summary>A file the site's own transport brings over, in the same part as any other, so it continues and is named the same way.</summary>
    private async Task<DownloadResult> ThroughTransportAsync(Job job, IFileTransport transport, CancellationToken cancel)
    {
        var (target, onEvent, file) = (job.Target, job.OnEvent, job.Signed.Current);
        var part = PartFile.PartPath(target.Path);
        var resumed = target.Saved is { } saved && saved.FileId == file.FileId;
        if (!resumed)
            PartFile.Delete(target.Path);
        PartFile.Save(target.Path, new PartState(file.Link.Key, file.FileId, file.Size ?? 0, ETag: null, LastModified: null, Segments: []));
        var done = new StrongBox<long>(resumed && File.Exists(part) ? new FileInfo(part).Length : 0);
        using var turn = await TurnAsync(job, cancel);
        onEvent(new DownloadStarted(target.Path, file.Size, done.Value, Connections: 1, resumed));
        var total = new StrongBox<long>(file.Size ?? -1);
        string? ending = null;
        var reporter = new ProgressReporter(() => Interlocked.Read(ref done.Value), () => Interlocked.Read(ref total.Value) is >= 0 and var known ? known : null, () => 1, onEvent, save: null);
        try
        {
            await Retry.RunAsync(Stage.Download, options.Retry, async tryCancel =>
            {
                await transport.TransferAsync(new TransportJob(part, bytes => Interlocked.Exchange(ref done.Value, bytes), job.Throttle.WaitAsync,
                                                               bytes => Interlocked.Exchange(ref total.Value, bytes), given => ending = given), tryCancel);
                // a transport that stops early without saying so is not to be believed about the file being complete
                if (file.Size is { } expected && Interlocked.Read(ref done.Value) < expected)
                    throw new StageException(Stage.Download, "The transfer ended before the whole file was here.", isRetryable: true);
                return true;
            }, cancel, DownloadRetrying.Reporter(onEvent), () => Interlocked.Read(ref done.Value));
        }
        finally
        {
            await reporter.DisposeAsync();
        }
        var final = target.Complete(ending);
        onEvent(new DownloadFinished(final, done.Value));
        return new DownloadResult(final, Skipped: false);
    }

    /// <summary>
    /// What the server says the file is: its size, its version, and whether ranges work. Resolving the link may have
    /// asked already, unless that is known to be out of date; otherwise ask for the first byte.
    /// </summary>
    private async Task<Probe> ProbeAsync(Job job, CancellationToken cancel)
    {
        if (!job.AskAgain && Probe.From(job.Signed.Current) is { } known)
            return known;
        using var turn = await TurnAsync(job, cancel);
        return await Retry.RunAsync(Stage.Download, options.Retry, async tryCancel =>
        {
            using var watch = new StallWatch(options.StallTimeout, Stage.Download, tryCancel);
            try
            {
                using var response = await SendAsync(job.Signed, 0, 0, watch);
                return Probe.Of(response);
            }
            catch (StageException e) when (e.Status == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // no first byte to give: the file may be empty, or the server may only be saying so; the whole of it tells
                using var whole = await SendAsync(job.Signed, null, null, watch);
                return Probe.Of(whole);
            }
        }, cancel, DownloadRetrying.Reporter(job.OnEvent));
    }

    private async Task<(long Bytes, string? Sha256)> SegmentsAsync(Job job, Probe probe, long total, PartState? saved, CancellationToken cancel)
    {
        var (target, onEvent, file) = (job.Target, job.OnEvent, job.Signed.Current);
        var plan = Plan(target, total, saved, onEvent);
        var state = new PartState(file.Link.Key, file.FileId, total, probe.ETag, probe.LastModified, Segments: []);
        var writer = new PartWriter(target.Path, plan, state, job.Throttle, Stage.Download, options.StallTimeout, onEvent, job.Hash);
        var sha256 = await writer.RunAsync(options.Connections, stop => RunConnectionsAsync(job, writer, probe, stop), cancel);
        return (total, sha256);
    }

    /// <summary>The saved parts to continue, or new ones when there are none or they can't be used.</summary>
    private SegmentPlan Plan(DownloadTarget target, long total, PartState? saved, Action<DownloadEvent> onEvent)
    {
        if (saved is not null)
        {
            try
            {
                return SegmentPlan.Resume(target.Path, saved, total, options.MinSegmentBytes);
            }
            catch (InvalidDataException e)
            {
                PartFile.Delete(target.Path);
                onEvent(new DownloadRestarted(e.Message));
            }
        }
        return SegmentPlan.Split(total, options.Connections, options.MinSegmentBytes);
    }

    /// <summary>
    /// Run the connections until every part is done. Each takes its turn at the host only for a try, not while it
    /// waits to retry, so a connection in back-off holds no slot. A connection the server calls busy (503, 429) leaves
    /// its part instead of retrying, as long as another is still allowed, so a CDN that limits connections gets fewer;
    /// the last one retries, with longer waits. A part left that way is taken up by the others when they finish their
    /// own, or by another round of those still allowed should they all have finished already.
    /// </summary>
    private async Task RunConnectionsAsync(Job job, PartWriter writer, Probe probe, CancellationToken cancel)
    {
        var allowed = new StrongBox<int>(options.Connections);
        bool StandDown(StageException failure)
        {
            if (!failure.IsBusy)
                return false;
            for (var now = Volatile.Read(ref allowed.Value); now > 1; now = Volatile.Read(ref allowed.Value))
            {
                if (Interlocked.CompareExchange(ref allowed.Value, now - 1, now) == now)
                {
                    job.OnEvent(new DownloadConnectionsReduced(now - 1, failure.Message));
                    return true;
                }
            }
            return false;
        }

        var source = RangeSource(job, probe);
        int before;
        do
        {
            before = Volatile.Read(ref allowed.Value);
            await writer.RunConnectionsAsync(before, options.Retry, async (segment, tryCancel) =>
            {
                using var turn = await TurnAsync(job, tryCancel);
                await writer.FetchAsync(segment, source, tryCancel);
            }, cancel, StandDown);
        }
        while (!writer.Plan.IsComplete && Volatile.Read(ref allowed.Value) != before);
    }

    /// <summary>The bytes of a segment, as the server sends them for a Range request, checked to be the file the probe saw.</summary>
    private PartWriter.Source RangeSource(Job job, Probe probe) => async (from, end, watch) =>
    {
        var response = await SendAsync(job.Signed, from, end - 1, watch);
        try
        {
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != from)
                throw new RangesIgnoredException();
            if (range.Length != probe.Total || !probe.SameVersion(response))
                throw new FileChangedException("The file changed on the server during the download.");
            return await response.Content.ReadAsStreamAsync(watch.Token); // disposing the stream ends the connection, as disposing the response would
        }
        catch
        {
            response.Dispose();
            throw;
        }
    };

    /// <summary>One connection from start to end, for a server that won't send parts; a failed try starts over.</summary>
    private async Task<(long Bytes, string? Sha256)> StreamAsync(Job job, Probe probe, CancellationToken cancel)
    {
        var (target, onEvent) = (job.Target, job.OnEvent);
        var part = PartFile.PartPath(target.Path);
        var written = new StrongBox<long>();
        using var turn = await TurnAsync(job, cancel);
        onEvent(new DownloadStarted(target.Path, probe.Total, 0, Connections: 1, Resumed: false));
        var reporter = new ProgressReporter(() => Interlocked.Read(ref written.Value), probe.Total, () => 1, onEvent, save: null);
        try
        {
            return await Retry.RunAsync(Stage.Download, options.Retry, async tryCancel =>
            {
                Interlocked.Exchange(ref written.Value, 0);
                using var handle = File.OpenHandle(part, FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous);
                using var watch = new StallWatch(options.StallTimeout, Stage.Download, tryCancel);
                using var response = await SendAsync(job.Signed, null, null, watch);
                if (!probe.SameVersion(response))
                    throw new FileChangedException("The file changed on the server during the download.");
                await using var body = await response.Content.ReadAsStreamAsync(tryCancel);
                using var hash = job.Hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (await watch.ReadAsync(body, buffer.AsMemory(0, job.Throttle.ChunkFor(BufferSize))) is var read and > 0)
                    {
                        await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), written.Value, tryCancel);
                        hash?.AppendData(buffer, 0, read);
                        Interlocked.Add(ref written.Value, read);
                        await job.Throttle.WaitAsync(read, tryCancel);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                if (probe.Total is { } total && written.Value != total)
                    throw new StageException(Stage.Download, "The connection closed before the file was complete.", isRetryable: true);
                return (written.Value, hash is null ? null : Convert.ToHexStringLower(hash.GetHashAndReset()));
            }, cancel, DownloadRetrying.Reporter(onEvent));
        }
        catch
        {
            File.Delete(part); // nothing to continue from without parts
            throw;
        }
        finally
        {
            await reporter.DisposeAsync();
        }
    }

    /// <summary>
    /// GET the file, or bytes [<paramref name="from"/>, <paramref name="to"/>] of it. A refused link is replaced by a
    /// new one and the try fails as retryable, so the next try uses it. Only the server's silence counts as a stall: a
    /// site asked for a new link has its own budget.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(SignedFile signed, long? from, long? to, StallWatch watch)
    {
        var file = await signed.NextAsync(watch.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        request.Headers.UserAgent.ParseAdd(file.UserAgent);
        request.Headers.Referrer = file.Referer;
        // a login is for the link's own site, not a CDN a site's link resolved to
        if (HttpFetcher.IsSameSite(file.Url, file.Link.Original))
            signed.Details.ApplyTo(request);
        if (file.Cookie is { } cookie)
            request.Headers.TryAddWithoutValidation(RequestDetails.CookieHeader, cookie);
        if (from is not null)
            request.Headers.Range = new RangeHeaderValue(from, to);
        HttpResponseMessage response;
        try
        {
            response = await watch.WaitAsync(stall => http.SendAsync(request, Stage.Download, stall, asFile: true));
        }
        catch (StageException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Gone)
        {
            await signed.RefreshAsync(file, watch.Token);
            throw new StageException(Stage.Download, "The download link expired; continuing with a new one.", isRetryable: true, inner: e);
        }
        if (!file.IsWebPage && LinkProbe.IsWebPage(response))
        {
            response.Dispose();
            throw new StageException(Stage.Download, "The server sent a web page instead of the file. The file may have been removed.");
        }
        signed.Accepted(file);
        return response;
    }

    /// <summary>Wait until the host may take one more connection, when connections per host are capped.</summary>
    private ValueTask<HostConnections.Turn> TurnAsync(Job job, CancellationToken cancel) =>
        hosts is null ? default : hosts.WaitAsync(job.Signed.Current.Url.Host, cancel);

    /// <summary>One file's download: where it goes, its link, its speed limits, and who hears about it.</summary>
    /// <param name="Hash">something needs the file's SHA-256</param>
    /// <param name="AskAgain">what resolving learned about the file is out of date: it changed on the server</param>
    private sealed record Job(DownloadTarget Target, SignedFile Signed, Throttle Throttle, Action<DownloadEvent> OnEvent, bool Hash, bool AskAgain = false);

    /// <summary>What the server said about the file before the download started.</summary>
    private sealed record Probe(long? Total, bool Ranges, string? ETag, DateTimeOffset? LastModified)
    {
        /// <summary>What resolving the link learned, when it asked the server itself.</summary>
        public static Probe? From(ResolvedFile file) =>
            file.Probe is { } known ? new Probe(file.Size, known.Ranges, known.ETag, known.LastModified) : null;

        /// <summary>What the answer to a request for the first byte says.</summary>
        public static Probe Of(HttpResponseMessage response)
        {
            var (etag, modified) = (response.Headers.ETag?.Tag, response.Content.Headers.LastModified);
            if (response.StatusCode != HttpStatusCode.PartialContent)
                return new Probe(response.Content.Headers.ContentLength, Ranges: false, etag, modified);
            return response.Content.Headers.ContentRange is { HasLength: true, Length: { } length }
                ? new Probe(length, Ranges: true, etag, modified)
                : new Probe(null, Ranges: false, etag, modified);
        }

        public bool Continues(PartState saved) =>
            Total == saved.Total && ETag == saved.ETag && LastModified == saved.LastModified;

        /// <summary>Whether <paramref name="response"/> is of the same file: judged by what both it and the probe say.</summary>
        public bool SameVersion(HttpResponseMessage response) =>
            (ETag is null || response.Headers.ETag?.Tag is not { } tag || tag == ETag)
            && (LastModified is null || response.Content.Headers.LastModified is not { } modified || modified == LastModified);
    }

    /// <summary>What is on disk can't be continued: the file on the server is not the one it came from.</summary>
    private sealed class FileChangedException(string message) : Exception(message);

    /// <summary>The server answered a request for a part with the whole file, after saying it sends parts.</summary>
    private sealed class RangesIgnoredException() : Exception("The server stopped sending the file in parts.");
}
