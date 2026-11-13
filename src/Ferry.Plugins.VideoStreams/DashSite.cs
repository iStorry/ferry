using System.Diagnostics.CodeAnalysis;
using Ferry.Core;
using Ferry.Core.Download;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Sites;
using Ferry.Core.Tools;

namespace Ferry.Plugins.VideoStreams;

/// <summary>Links to a DASH manifest: its best quality. Video and sound that are separate files need ffmpeg, which the user installs, to become one.</summary>
public sealed class DashSite(HttpFetcher http, string userAgent, string ffmpeg) : ISite
{
    public const string SiteId = "dash";

    private const string ManifestEnding = ".mpd";

    public string Id => SiteId;

    public string Name => "Video Streams";

    public bool Claims(Uri link) => LinkProbe.IsWebLink(link) && link.AbsolutePath.EndsWith(ManifestEnding, StringComparison.OrdinalIgnoreCase);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = new FileLink(SiteId, link, LinkKeys.WithoutFragment(link));
        reason = null;
        return true;
    }

    public async Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        var manifest = await Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, async tryCancel =>
            DashManifest.Parse(await http.GetTextAsync(file.Original, userAgent, HttpAnswers.MaxPageBytes, Stage.Resolve, tryCancel, details), file.Original), cancel);
        var title = Path.GetFileNameWithoutExtension(FileLink.LastSegment(file.Original));
        return new ResolvedFile(file, file.Key, FileNames.Sanitize($"{(title.Length > 0 ? title : "video")}{manifest.Container.Ending}"), file.Original, Referer: null, userAgent,
                                ExpiresAt: null, IsWebPage: false, Size: null,
                                new JoinedTransport(new SegmentTransport(http, userAgent, file, manifest.Video, details),
                                                    manifest.Audio is { } audio ? new SegmentTransport(http, userAgent, file, audio, details) : null, ffmpeg, manifest.Container));
    }
}

/// <summary>
/// One track goes straight into the part. Two go into a folder in its place, each picking up where it stopped, then ffmpeg joins them
/// without changing either, and the folder gives way to the one file.
/// </summary>
internal sealed class JoinedTransport(SegmentTransport video, SegmentTransport? audio, string ffmpeg, MediaContainer container) : IFileTransport
{
    private const string NeedsTool = "This video's picture and sound are separate files, which need ffmpeg to become one. Install it, then set where it is in Video Streams' options.";

    public async Task TransferAsync(TransportJob job, CancellationToken cancel)
    {
        if (audio is null)
        {
            await video.TransferAsync(job, cancel);
            return;
        }
        await RunToolAsync(["-version"], cancel); // before anything is downloaded: without it, the two can't become one
        Directory.CreateDirectory(job.PartPath);
        var (videoPath, audioPath, joined) = (Path.Combine(job.PartPath, "video"), Path.Combine(job.PartPath, "audio"), Path.Combine(job.PartPath, "joined" + container.Ending));
        long videoBytes = 0, audioBytes = 0;
        await video.TransferAsync(job with { PartPath = videoPath, OnBytes = bytes => job.OnBytes((videoBytes = bytes) + audioBytes) }, cancel);
        await audio.TransferAsync(job with { PartPath = audioPath, OnBytes = bytes => job.OnBytes(videoBytes + (audioBytes = bytes)) }, cancel);
        await RunToolAsync(["-y", "-loglevel", "error", "-i", videoPath, "-i", audioPath, "-c", "copy", "-f", container.Format, joined], cancel);
        File.Delete(videoPath);
        File.Delete(audioPath);
        PartFile.BecomeOneFile(job.PartPath);
    }

    private Task RunToolAsync(IEnumerable<string> arguments, CancellationToken cancel) =>
        ToolProcess.RunAsync(ffmpeg, arguments, onStdoutLine: null, Stage.Download, cancel, ifMissing: NeedsTool);
}
