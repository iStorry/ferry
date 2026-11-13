using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Ferry.Core;
using Ferry.Core.Download;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.VideoStreams;

/// <summary>
/// Links to an HLS playlist: its best quality, its pieces joined into one file that plays as it is. A stream whose sound
/// is a playlist of its own needs ffmpeg, which the user installs, to become one file.
/// </summary>
/// <param name="ffmpeg">the ffmpeg program; the setting's default when not given</param>
public sealed class HlsSite(HttpFetcher http, string userAgent, string? ffmpeg = null) : ISite
{
    public const string SiteId = "hls";

    private const string PlaylistEnding = ".m3u8";
    private const int MostPlaylistHops = 3;

    private readonly string _ffmpeg = ffmpeg ?? VideoStreamsPlugin.Ffmpeg.Default;

    public string Id => SiteId;

    public string Name => "Video Streams";

    public bool Claims(Uri link) => LinkProbe.IsWebLink(link) && link.AbsolutePath.EndsWith(PlaylistEnding, StringComparison.OrdinalIgnoreCase);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = new FileLink(SiteId, link, LinkKeys.WithoutFragment(link));
        reason = null;
        return true;
    }

    public async Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        var (at, playlist, audioAt) = (file.Original, await ReadAsync(file.Original, file, details, cancel), (Uri?)null);
        for (var hops = 0; playlist.Best is { } best && hops < MostPlaylistHops; hops++)
        {
            audioAt ??= playlist.Audio;
            (at, playlist) = (best, await ReadAsync(best, file, details, cancel));
        }
        var video = Pieces(playlist);
        var audio = audioAt is null ? null : Pieces(await ReadAsync(audioAt, file, details, cancel));
        var title = Path.GetFileNameWithoutExtension(FileLink.LastSegment(file.Original));
        var ending = audio is not null || video.Init is not null ? MediaContainer.Mp4.Ending : ".ts";
        return new ResolvedFile(file, file.Key, FileNames.Sanitize($"{(title.Length > 0 ? title : "video")}{ending}"), at, Referer: null, userAgent, ExpiresAt: null,
                                IsWebPage: false, Size: null,
                                new JoinedTransport(new SegmentTransport(http, userAgent, file, video, details),
                                                    audio is null ? null : new SegmentTransport(http, userAgent, file, audio, details), _ffmpeg, MediaContainer.Mp4));
    }

    /// <summary>A playlist of pieces, as one Ferry can download to the end of.</summary>
    private static HlsPlaylist Pieces(HlsPlaylist playlist)
    {
        if (playlist.Segments.Count == 0)
            throw new StageException(Stage.Resolve, "This playlist has no video in it.");
        if (playlist.IsLive)
            throw new StageException(Stage.Resolve, "This stream is live, so it has no end to download to.");
        return playlist;
    }

    /// <summary>A login goes with the playlist only on the link's own site.</summary>
    private Task<HlsPlaylist> ReadAsync(Uri url, FileLink file, RequestDetails details, CancellationToken cancel) =>
        Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, async tryCancel =>
            HlsPlaylist.Parse(await http.GetTextAsync(url, userAgent, HttpAnswers.MaxPageBytes, Stage.Resolve, tryCancel,
                                                      HttpFetcher.IsSameSite(url, file.Original) ? details : null), url), cancel);
}

/// <summary>
/// Fetches the pieces in order into the part, a few requested ahead so that no piece waits for the server to answer. Each
/// piece streams straight from the connection into the part, through its decryption when it has one. How many are done is
/// kept beside the part, so a stopped download carries on.
/// </summary>
internal sealed class SegmentTransport(HttpFetcher http, string userAgent, FileLink file, SegmentList track, RequestDetails details) : IFileTransport
{
    private const int KeyBytes = 16;
    private const int PiecesAhead = 4;
    private const int CopyBufferBytes = 1 << 16;

    private readonly Dictionary<Uri, byte[]> _keys = [];

    public async Task TransferAsync(TransportJob job, CancellationToken cancel)
    {
        var (nextPiece, bytes) = await LoadProgressAsync(job.PartPath, cancel);
        await using var part = new FileStream(job.PartPath, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, Options = FileOptions.Asynchronous });
        part.SetLength(bytes); // whatever came after the last piece that was finished is cut off
        part.Position = bytes;
        using var fetching = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var ahead = new Queue<(MediaSegment Piece, int? Done, Task<HttpResponseMessage> Response)>();
        using var fetches = Fetches(nextPiece).GetEnumerator();
        try
        {
            for (Prefetch(ahead, fetches, fetching.Token); ahead.TryDequeue(out var next); Prefetch(ahead, fetches, fetching.Token))
            {
                using (var response = await next.Response)
                    await WriteAsync(part, response, next.Piece, job, cancel);
                if (next.Done is { } done)
                {
                    await part.FlushAsync(cancel);
                    await SaveProgressAsync(job.PartPath, done, part.Position, cancel);
                }
            }
        }
        finally
        {
            await fetching.CancelAsync();
            await DropAsync(ahead.Select(waiting => waiting.Response));
        }
        File.Delete(PartFile.ProgressPath(job.PartPath));
    }

    /// <summary>What is to be fetched from piece <paramref name="from"/> on, with which piece each item completes (an initialization section completes none).</summary>
    private IEnumerable<(MediaSegment Piece, int? Done)> Fetches(int from)
    {
        for (var i = from; i < track.Segments.Count; i++)
        {
            var piece = track.Segments[i];
            if (piece.Map is { } map)
                yield return (map, null);
            yield return (piece, i + 1);
        }
    }

    private void Prefetch(Queue<(MediaSegment Piece, int? Done, Task<HttpResponseMessage> Response)> ahead, IEnumerator<(MediaSegment Piece, int? Done)> fetches, CancellationToken cancel)
    {
        while (ahead.Count < PiecesAhead && fetches.MoveNext())
            ahead.Enqueue((fetches.Current.Piece, fetches.Current.Done, FetchAsync(fetches.Current.Piece.Url, fetches.Current.Piece.Range, cancel)));
    }

    /// <summary>Requests that were made ahead of a failure or a cancellation are let go: the first failure is the one reported.</summary>
    private static async Task DropAsync(IEnumerable<Task<HttpResponseMessage>> pending)
    {
        foreach (var fetch in pending)
        {
            try
            {
                (await fetch).Dispose();
            }
            catch (Exception e) when (e is StageException or OperationCanceledException)
            {
                // cancelled, or failed behind the piece whose failure is being reported
            }
        }
    }

    private async Task WriteAsync(FileStream part, HttpResponseMessage response, MediaSegment piece, TransportJob job, CancellationToken cancel)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        if (piece.Key is not { } keyUrl || piece.Iv is not { } iv)
        {
            await CopyAsync(body, part, job, cancel);
            return;
        }
        using var aes = Aes.Create();
        aes.Key = await KeyAsync(keyUrl, cancel);
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        await using var plain = new CryptoStream(body, decryptor, CryptoStreamMode.Read);
        try
        {
            await CopyAsync(plain, part, job, cancel);
        }
        catch (CryptographicException e)
        {
            throw new StageException(Stage.Download, "A piece of the stream couldn't be decrypted.", inner: e);
        }
    }

    private static async Task CopyAsync(Stream from, FileStream part, TransportJob job, CancellationToken cancel)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await from.ReadAsync(buffer, cancel);
                }
                catch (IOException e)
                {
                    throw new StageException(Stage.Download, $"The connection dropped while reading a piece: {e.Message}", isRetryable: true, inner: e);
                }
                if (read == 0)
                    return;
                await part.WriteAsync(buffer.AsMemory(0, read), cancel);
                job.OnBytes(part.Position);
                await job.Pace(read, cancel);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<byte[]> KeyAsync(Uri url, CancellationToken cancel)
    {
        if (_keys.TryGetValue(url, out var known))
            return known;
        byte[] key;
        using (var response = await FetchAsync(url, null, cancel))
            key = await HttpFetcher.ReadBytesAsync(response, HttpAnswers.MaxApiBytes, Stage.Download, cancel);
        if (key.Length != KeyBytes)
            throw new StageException(Stage.Download, "The stream's key isn't an AES-128 key.");
        return _keys[url] = key;
    }

    /// <summary>The answer, its headers read, for the caller to stream; a login goes along only on the link's own site.</summary>
    /// <exception cref="StageException">asked for some bytes of the file, the server sent something else</exception>
    private async Task<HttpResponseMessage> FetchAsync(Uri url, (long From, long To)? range, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        if (HttpFetcher.IsSameSite(url, file.Original))
            details.ApplyTo(request);
        if (range is { } bytes)
            request.Headers.Range = new RangeHeaderValue(bytes.From, bytes.To);
        var response = await http.SendAsync(request, Stage.Download, cancel, asFile: true);
        if (range is not { } asked || IsThoseBytes(response, asked))
            return response;
        response.Dispose();
        throw new StageException(Stage.Download, $"{url.Host} sent something other than bytes {asked.From}-{asked.To} of a piece, which would corrupt the video.");
    }

    private static bool IsThoseBytes(HttpResponseMessage response, (long From, long To) asked) =>
        response.StatusCode == HttpStatusCode.PartialContent
        && response.Content.Headers.ContentRange is { From: { } from, To: { } to } && (from, to) == asked;

    private static async Task<(int Next, long Bytes)> LoadProgressAsync(string part, CancellationToken cancel)
    {
        var path = PartFile.ProgressPath(part);
        if (!File.Exists(path) || !File.Exists(part))
            return (0, 0);
        var words = (await File.ReadAllTextAsync(path, Encoding.ASCII, cancel)).Split(' ');
        return words.Length == 2 && int.TryParse(words[0], out var next) && long.TryParse(words[1], out var bytes) && bytes <= new FileInfo(part).Length
            ? (next, bytes)
            : (0, 0);
    }

    private static Task SaveProgressAsync(string part, int next, long bytes, CancellationToken cancel) =>
        File.WriteAllTextAsync(PartFile.ProgressPath(part), FormattableString.Invariant($"{next} {bytes}"), Encoding.ASCII, cancel);
}
