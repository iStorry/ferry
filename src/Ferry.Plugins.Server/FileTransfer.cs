using System.Diagnostics;
using Ferry.Core;
using Ferry.Core.Download;
using Ferry.Core.Files;
using Ferry.Core.Http;

namespace Ferry.Plugins.Server;

/// <summary>
/// Where finished files on the server are read from: the agent itself over ssh in server mode (each read its own
/// connection), the local disk in tests.
/// </summary>
public interface IRemoteFiles
{
    /// <summary><paramref name="length"/> bytes of <paramref name="file"/> from <paramref name="offset"/>; the stream
    /// ends after them. Opening may wait its turn: only reading the stream is watched for stalls.</summary>
    /// <exception cref="StageException">the file can't be read (<see cref="Stage.Transfer"/>; retryable when trying
    /// again may work), at the open or at the end of the stream</exception>
    Task<Stream> OpenReadAsync(RemoteFile file, long offset, long length, CancellationToken cancel);
}

/// <summary>A file finished on the server, as its agent describes it.</summary>
/// <param name="Item">its id in the agent's queue, which is how the agent is asked for its bytes</param>
/// <param name="Slug">the file link's slug, which identifies its unfinished transfer</param>
/// <param name="Path">where it is on the server; its name is the name it gets here</param>
/// <param name="Sha256">of the whole file, computed on the server, in hex</param>
public sealed record RemoteFile(Guid Item, string Slug, string Path, long Size, string Sha256)
{
    /// <summary>The file's name on the server, already safe on every OS (so it has neither kind of slash in it).</summary>
    public string Name => Path[(Path.LastIndexOfAny(['/', '\\']) + 1)..];
}

/// <param name="Retry">how often a dropped transfer is tried again, counted afresh whenever it made progress</param>
/// <param name="StallTimeout">a transfer that receives nothing for this long is tried again</param>
/// <param name="Limit">the speed limit shared by every transfer</param>
/// <param name="Connections">connections per file, each bringing over its own part: one connection over a long or
/// lossy route is often much slower than the line</param>
/// <param name="MinSegmentBytes">a file is split into parts of at least this size, and a smaller one comes over on one
/// connection: each part is an ssh login of its own (about 3 s on a far server), which a small part doesn't repay</param>
/// <param name="Place">the user's rules for where a file goes and what it is called</param>
public sealed record TransferOptions(RetryPolicy Retry, TimeSpan StallTimeout, ExistingFile OnExisting, SpeedLimit? Limit = null,
                                     int Connections = 1, long MinSegmentBytes = 32 << 20, Func<string, string, (string Folder, string Name)>? Place = null);

/// <summary>
/// Brings a file over from the server into "{name}.ferry-part", in parts over several connections, continuing after
/// a dropped connection (in this run or a later one) from what is already on disk. The file is hashed as it arrives,
/// and it gets its final name only if the SHA-256 matches the server's; otherwise it comes over once more, and then
/// fails.
/// </summary>
internal sealed class FileTransfer(IRemoteFiles files, TransferOptions options)
{
    // marks a part file as a transfer's rather than a download's, which has the CDN's ETag instead of a SHA-256
    private const string TransferFileId = "transfer";

    /// <exception cref="StageException">the transfer failed (<see cref="Stage.Transfer"/>)</exception>
    /// <exception cref="IOException">the file could not be written</exception>
    public async Task<DownloadResult> RunAsync(RemoteFile file, string folder, Action<DownloadEvent> onEvent, CancellationToken cancel)
    {
        var name = FileNames.Sanitize(file.Name);
        if (options.Place is { } place)
            (folder, name) = place(folder, name);
        Directory.CreateDirectory(folder);
        var wanted = Path.Combine(folder, name);
        if (await AlreadyHereAsync(wanted, file, cancel) is { } here)
        {
            onEvent(new DownloadStarted(here, file.Size, file.Size, Connections: 1, Resumed: true));
            onEvent(new DownloadFinished(here, file.Size));
            return new DownloadResult(here, Skipped: false, file.Sha256);
        }
        var target = DownloadTarget.Choose(wanted, file.Slug, options.OnExisting);
        if (target.Skip)
        {
            onEvent(new DownloadSkipped(target.Path));
            return new DownloadResult(target.Path, Skipped: true);
        }
        try
        {
            string? hash;
            for (var round = 1; ; round++)
            {
                hash = await TransferAsync(target, file, onEvent, cancel);
                if (string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    break;
                PartFile.Delete(target.Path);
                if (round == 2)
                    throw new StageException(Stage.Transfer, "The file that arrived doesn't match the one on the server (SHA-256), twice.");
                onEvent(new DownloadRestarted("The file that arrived doesn't match the one on the server; bringing it over again."));
                target = target with { Saved = null };
            }
            var final = target.Complete();
            onEvent(new DownloadFinished(final, file.Size));
            return new DownloadResult(final, Skipped: false, hash);
        }
        finally
        {
            DownloadTarget.Release(target.Path);
        }
    }

    /// <summary>
    /// This very file under its name or one of the names it would get beside it: brought over before, by a run that ended
    /// before the server heard, so the server still offers it. It doesn't come over twice.
    /// </summary>
    private static async Task<string?> AlreadyHereAsync(string wanted, RemoteFile file, CancellationToken cancel)
    {
        foreach (var candidate in FileNames.Candidates(wanted))
        {
            var there = new FileInfo(candidate);
            if (!there.Exists)
                return null;
            if (there.Length == file.Size && string.Equals(await FileChecksum.Sha256Async(candidate, cancel), file.Sha256, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        throw new UnreachableException("FileNames.Candidates never runs out.");
    }

    /// <summary>Bring the rest of the file into its part file; returns the SHA-256 of the whole part.</summary>
    private Task<string?> TransferAsync(DownloadTarget target, RemoteFile file, Action<DownloadEvent> onEvent, CancellationToken cancel)
    {
        var connections = Math.Max(1, options.Connections);
        var state = new PartState(file.Slug, TransferFileId, file.Size, file.Sha256, LastModified: null, Segments: []);
        var writer = new PartWriter(target.Path, Plan(target, file, onEvent), state, new Throttle(options.Limit, null), Stage.Transfer, options.StallTimeout, onEvent, hash: true);
        // the part as it is now; if another connection takes over its end meanwhile, this one stops early. Opening is
        // no stall, as it may be waiting its turn to log in: the reads are what the server is watched by
        PartWriter.Source source = (from, end, watch) => files.OpenReadAsync(file, from, end - from, watch.Token);
        return writer.RunAsync(connections, stop => writer.RunConnectionsAsync(connections, options.Retry,
                                                                                (segment, tryCancel) => writer.FetchAsync(segment, source, tryCancel), stop), cancel);
    }

    /// <summary>The parts an earlier transfer of this same file left, or new ones to start afresh.</summary>
    private SegmentPlan Plan(DownloadTarget target, RemoteFile file, Action<DownloadEvent> onEvent)
    {
        if (target.Saved is { FileId: TransferFileId } saved && saved.Total == file.Size
            && string.Equals(saved.ETag, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return SegmentPlan.Resume(target.Path, saved, file.Size, options.MinSegmentBytes);
            }
            catch (InvalidDataException e)
            {
                onEvent(new DownloadRestarted(e.Message));
            }
        }
        PartFile.Delete(target.Path); // a download's part, or another version of the file
        return SegmentPlan.Split(file.Size, Math.Max(1, options.Connections), options.MinSegmentBytes);
    }
}
