using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ferry.Core.Http;
using Ferry.Core.Sites;
using Ferry.Plugins.VideoStreams;

namespace Ferry.Core.Tests;

/// <summary>HLS and DASH as the Video Streams plugin reads and fetches them, against a server of this test's own.</summary>
public sealed class VideoStreamTests
{
    private static readonly Uri At = new("https://x.example/v/m.mpd");

    [Fact]
    public void AnOpenEndedTimelineEntryRunsToTheNextOne()
    {
        var manifest = DashManifest.Parse("""
            <MPD type="static" mediaPresentationDuration="PT30S">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/webm">
                  <Representation id="v" bandwidth="100">
                    <SegmentTemplate media="$Time$.webm" timescale="1">
                      <SegmentTimeline><S t="0" d="5" r="-1" /><S t="20" d="5" r="1" /></SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """, At);
        Assert.Equal(["0", "5", "10", "15", "20", "25"], manifest.Video.Segments.Select(piece => Path.GetFileNameWithoutExtension(piece.Url.AbsolutePath)));
        Assert.Equal(MediaContainer.WebM, manifest.Container);
    }

    [Fact]
    public void AnOpenEndedTimelineEntryRunsToThePeriodsEnd()
    {
        var manifest = DashManifest.Parse("""
            <MPD type="static" mediaPresentationDuration="PT12S">
              <Period>
                <AdaptationSet contentType="video">
                  <Representation id="v" bandwidth="100">
                    <SegmentTemplate media="$Time$.m4s" initialization="init.mp4" timescale="2">
                      <SegmentTimeline><S t="0" d="5" r="-1" /></SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """, At);
        Assert.Equal(["0", "5", "10", "15", "20"], manifest.Video.Segments.Select(piece => Path.GetFileNameWithoutExtension(piece.Url.AbsolutePath)));
        Assert.Equal(new Uri("https://x.example/v/init.mp4"), manifest.Video.Init);
        Assert.Equal(MediaContainer.Mp4, manifest.Container);
    }

    [Fact]
    public void AByteOrderMarkIsNoPartOfAPlaylist()
    {
        var playlist = HlsPlaylist.Parse("\uFEFF#EXTM3U\n#EXTINF:4,\none.ts\n#EXT-X-ENDLIST\n", new Uri("https://x.example/v.m3u8"));
        Assert.Equal([new Uri("https://x.example/one.ts")], playlist.Segments.Select(piece => piece.Url));
    }

    [Fact]
    public void TheBestVariantsOwnAudioPlaylistIsFound()
    {
        const string Master = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="Commentary",URI="audio/commentary.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="English",DEFAULT=YES,URI="audio/en.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="muxed",NAME="English",DEFAULT=YES
            #EXT-X-STREAM-INF:BANDWIDTH=100,AUDIO="muxed"
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=900,AUDIO="aac"
            high.m3u8
            """;
        var playlist = HlsPlaylist.Parse(Master, new Uri("https://x.example/a/master.m3u8"));
        Assert.Equal(new Uri("https://x.example/a/high.m3u8"), playlist.Best);
        Assert.Equal(new Uri("https://x.example/a/audio/en.m3u8"), playlist.Audio);

        var muxed = HlsPlaylist.Parse(Master.Replace("BANDWIDTH=900,AUDIO=\"aac\"", "BANDWIDTH=900,AUDIO=\"muxed\"", StringComparison.Ordinal), new Uri("https://x.example/a/master.m3u8"));
        Assert.Null(muxed.Audio);
    }

    [Fact]
    public void InitializationSectionsGoBeforeTheFirstPieceAndAfterEachChange()
    {
        var playlist = HlsPlaylist.Parse("""
            #EXTM3U
            #EXT-X-MAP:URI="init.mp4",BYTERANGE="720@0"
            #EXTINF:4,
            one.m4s
            #EXT-X-DISCONTINUITY
            #EXT-X-MAP:URI="init2.mp4"
            #EXTINF:4,
            two.m4s
            #EXTINF:4,
            three.m4s
            #EXT-X-ENDLIST
            """, new Uri("https://x.example/v.m3u8"));
        Assert.Equal(3, playlist.Segments.Count);
        Assert.Equal(new MediaSegment(new Uri("https://x.example/init.mp4"), (0, 719), null, null), playlist.Segments[0].Map);
        Assert.Equal(new Uri("https://x.example/init2.mp4"), playlist.Segments[1].Map?.Url);
        Assert.Null(playlist.Segments[2].Map);
        Assert.Equal(new Uri("https://x.example/init.mp4"), playlist.Init);
    }

    [Fact]
    public async Task PiecesStreamIntoThePartInOrderDecryptedWhereTheyAreEncrypted()
    {
        var (key, iv) = (RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16));
        var secret = Encoding.ASCII.GetBytes("the second piece, which is encrypted");
        using var aes = Aes.Create();
        aes.Key = key;
        using var server = new PieceServer(new Dictionary<string, byte[]>
        {
            ["/init"] = "INIT|"u8.ToArray(),
            ["/one"] = "one|"u8.ToArray(),
            ["/two"] = aes.EncryptCbc(secret, iv),
            ["/key"] = key,
        });
        var track = new SegmentList(
        [
            new MediaSegment(new Uri(server.Url, "/one"), null, null, null, new MediaSegment(new Uri(server.Url, "/init"), null, null, null)),
            new MediaSegment(new Uri(server.Url, "/two"), null, new Uri(server.Url, "/key"), iv),
        ]);
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var part = Path.Combine(folder, "v.ferry-part");
            await Transfer(track, part);
            Assert.Equal("INIT|one|the second piece, which is encrypted", File.ReadAllText(part));
            // the pieces are asked for ahead, all at once, so the server sees them in no fixed order
            Assert.Equal(["/init", "/key", "/one", "/two"], server.Requests.Select(request => request.Path).Order());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AWholeFileInPlaceOfTheBytesAskedForIsRefused()
    {
        using var server = new PieceServer(new Dictionary<string, byte[]> { ["/one"] = "0123456789abcdef"u8.ToArray() });
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var part = Path.Combine(folder, "v.ferry-part");
            var ranged = new SegmentList([new MediaSegment(new Uri(server.Url, "/one"), (4, 7), null, null)]);
            await Transfer(ranged, part);
            Assert.Equal("4567", File.ReadAllText(part));
            Assert.Equal("bytes=4-7", server.Requests.Single().Range);

            server.IgnoresRanges = true;
            File.Delete(part);
            var e = await Assert.ThrowsAsync<StageException>(() => Transfer(ranged, part));
            Assert.False(e.IsRetryable);
            Assert.Contains("bytes 4-7", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task Transfer(SegmentList track, string part)
    {
        using var http = new HttpFetcher();
        var transport = new SegmentTransport(http, "test", new FileLink(HlsSite.SiteId, new Uri("https://x.example/v.m3u8"), "k"), track, RequestDetails.None);
        await transport.TransferAsync(new TransportJob(part, _ => { }, (_, _) => ValueTask.CompletedTask, _ => { }, _ => { }), CancellationToken.None);
    }

    /// <summary>Serves pieces by path, honouring a Range header unless told to answer with the whole file instead.</summary>
    private sealed class PieceServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly IReadOnlyDictionary<string, byte[]> _pieces;

        public PieceServer(IReadOnlyDictionary<string, byte[]> pieces)
        {
            _pieces = pieces;
            (_listener, Url) = TestListener.Start();
            _ = ServeAsync();
        }

        public Uri Url { get; }

        public bool IgnoresRanges { get; set; }

        public List<(string Path, string? Range)> Requests { get; } = [];

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                using var response = context.Response;
                var path = context.Request.Url!.AbsolutePath;
                var range = context.Request.Headers["Range"];
                lock (Requests)
                    Requests.Add((path, range));
                if (!_pieces.TryGetValue(path, out var bytes))
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    continue;
                }
                if (range is not null && !IgnoresRanges)
                {
                    var ends = range["bytes=".Length..].Split('-').Select(int.Parse).ToArray();
                    response.StatusCode = (int)HttpStatusCode.PartialContent;
                    response.Headers["Content-Range"] = $"bytes {ends[0]}-{ends[1]}/{bytes.Length}";
                    bytes = bytes[ends[0]..(ends[1] + 1)];
                }
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
            }
        }
    }
}
