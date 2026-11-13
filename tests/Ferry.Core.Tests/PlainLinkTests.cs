using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ferry.Core;
using Ferry.Core.Download;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Queue;
using Ferry.Core.Sites;

namespace Ferry.Core.Tests;

/// <summary>A small web server for plain links: files with ranges, redirects, and what each request carried.</summary>
internal sealed class PlainServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _serving;

    public PlainServer()
    {
        (_listener, Url) = TestListener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public Uri Url { get; }

    public ConcurrentDictionary<string, (byte[] Data, string Type, string? Disposition)> Files { get; } = new();

    public ConcurrentDictionary<string, Uri> Redirects { get; } = new();

    public ConcurrentQueue<(string Path, NameValueCollection Headers)> Requests { get; } = new();

    /// <summary>A Set-Cookie header on every answer, as a server that tracks its visitors sends.</summary>
    public string? SetCookie { get; set; }

    /// <summary>Files go without a length, as a server that sends them in chunks does.</summary>
    public bool Chunked { get; set; }

    /// <summary>Asked about each request, by its path and Range header, before it is answered: it may change what is served, or give a status to answer with instead.</summary>
    public Func<string, string?, int?>? Intercept { get; set; }

    public Uri Add(string path, byte[] data, string type = "application/octet-stream", string? disposition = null)
    {
        Files[path] = (data, type, disposition);
        return new Uri(Url, path);
    }

    public Uri Redirect(string path, Uri to)
    {
        Redirects[path] = to;
        return new Uri(Url, path);
    }

    public IEnumerable<string?> Seen(string path, string header) =>
        Requests.Where(r => r.Path == path).Select(r => r.Headers[header]);

    public void Dispose()
    {
        _listener.Close();
        _serving.GetAwaiter().GetResult();
    }

    private async Task ServeAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or ArgumentException or InvalidOperationException)
            {
                return; // closed, perhaps before it got this far
            }
            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        var (request, response) = (context.Request, context.Response);
        var path = request.Url!.AbsolutePath;
        Requests.Enqueue((path, request.Headers));
        using (response)
        {
            if (SetCookie is { } cookie)
                response.Headers["Set-Cookie"] = cookie;
            if (Intercept?.Invoke(path, request.Headers["Range"]) is { } status)
            {
                response.StatusCode = status;
                return;
            }
            if (Redirects.TryGetValue(path, out var to))
            {
                response.StatusCode = 302;
                response.RedirectLocation = to.ToString();
                return;
            }
            if (!Files.TryGetValue(path, out var file))
            {
                response.StatusCode = 404;
                return;
            }
            response.ContentType = file.Type;
            response.Headers["Accept-Ranges"] = "bytes";
            if (file.Disposition is { } disposition)
                response.Headers["Content-Disposition"] = disposition;
            var (from, to2) = (0L, file.Data.Length - 1L);
            if (request.Headers["Range"] is { } range)
            {
                var parts = range["bytes=".Length..].Split('-');
                from = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                if (parts[1].Length > 0)
                    to2 = Math.Min(to2, long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                if (from > to2)
                {
                    // past the end, as every range of an empty file is
                    response.StatusCode = 416;
                    response.Headers["Content-Range"] = $"bytes */{file.Data.Length}";
                    return;
                }
                response.StatusCode = 206;
                response.Headers["Content-Range"] = $"bytes {from}-{to2}/{file.Data.Length}";
            }
            if (Chunked)
                response.SendChunked = true;
            else
                response.ContentLength64 = to2 - from + 1;
            try
            {
                await response.OutputStream.WriteAsync(file.Data.AsMemory((int)from, (int)(to2 - from + 1)));
            }
            catch (HttpListenerException)
            {
                // the downloader stopped reading: a part it no longer wants
            }
        }
    }
}

/// <summary>Plain links through the whole downloader: Ferry's own job, with no plugin at all.</summary>
public sealed class PlainLinkTests : IDisposable
{
    private const int MiB = 1 << 20;
    private const string Login = "session=secret";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private readonly string _folder = Directory.CreateTempSubdirectory("ferry_plain_").FullName;
    private readonly PlainServer _site = new();
    private readonly PlainServer _other = new();
    private readonly HttpFetcher _http = new();

    public void Dispose()
    {
        _http.Dispose();
        _site.Dispose();
        _other.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static byte[] Bytes(int size, int seed = 7)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private DownloadQueue Queue(Func<DownloadOptions, DownloadOptions>? change = null)
    {
        var options = DownloadOptions.For(DownloadProfile.Balanced, connections: 2, ExistingFile.Rename) with
        {
            Retry = RetryPolicy.Untimed(maxRetries: 2, wait: TimeSpan.FromMilliseconds(1)),
        };
        var sites = SiteCatalog.Standard(_http, "ferry-test", []);
        return new DownloadQueue(new SegmentedDownload(_http, sites, change?.Invoke(options) ?? options), sites, _folder, filesAtOnce: 2);
    }

    private static async Task<QueueItem> RunAsync(DownloadQueue queue, Uri link, AddOptions? options = null)
    {
        var added = await queue.AddAsync([link.ToString()], options);
        var id = Assert.Single(added.Added).Id;
        await queue.WhenIdle.WaitAsync(Patience);
        return queue.Find(id)!;
    }

    [Fact]
    public async Task APlainLinkDownloadsAndIsNamedFromItsPath()
    {
        var data = Bytes(3 * MiB);
        await using var queue = Queue();

        var item = await RunAsync(queue, _site.Add("/files/video.mp4", data));

        Assert.Equal(QueueState.Done, item.State);
        Assert.Equal(Path.Combine(_folder, "video.mp4"), item.Path);
        Assert.Equal(data, await File.ReadAllBytesAsync(item.Path!));
    }

    [Fact]
    public async Task APlainLinkIsAskedForItsFirstByteOnce()
    {
        await using var queue = Queue();

        await RunAsync(queue, _site.Add("/files/once.bin", Bytes(2 * MiB)));

        // resolving asked, and the download took its word rather than asking again
        Assert.Single(_site.Seen("/files/once.bin", "Range"), range => range == "bytes=0-0");
    }

    [Fact]
    public async Task ACookieTheServerSetsIsNotSentBack()
    {
        _site.SetCookie = "visitor=tracked";
        await using var queue = Queue();

        var item = await RunAsync(queue, _site.Add("/files/tracked.bin", Bytes(2 * MiB)));

        Assert.Equal(QueueState.Done, item.State);
        var cookies = _site.Seen("/files/tracked.bin", "Cookie").ToList();
        Assert.True(cookies.Count >= 2); // the probe and at least one part followed the answer that set it
        Assert.All(cookies, Assert.Null);
    }

    [Fact]
    public async Task AnEmptyFileIsSavedEmpty()
    {
        await using var queue = Queue();

        var item = await RunAsync(queue, _site.Add("/files/empty.bin", []));

        Assert.Equal(QueueState.Done, item.State);
        Assert.Equal(0, new FileInfo(item.Path!).Length);
    }

    private async Task<ResolvedFile> ResolveAsync(Uri url)
    {
        var sites = SiteCatalog.Standard(_http, "ferry-test", []);
        Assert.True(sites.TryParse(url.ToString(), out var link, out _));
        return await sites.ResolveAsync(link, RequestDetails.None, CancellationToken.None);
    }

    [Fact]
    public async Task A416ToTheFirstByteDoesNotMakeAFileEmpty()
    {
        var data = Bytes(2 * MiB);
        _site.Intercept = (_, range) => range == "bytes=0-0" ? 416 : null;
        var url = _site.Add("/refuses.bin", data);
        await using var queue = Queue();

        Assert.Equal(data.Length, (await ResolveAsync(url)).Size);
        var item = await RunAsync(queue, url);

        Assert.Equal(QueueState.Done, item.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(item.Path!));
    }

    [Fact]
    public async Task WithoutALengthOnlyAnEmptyBodyIsAnEmptyFile()
    {
        _site.Intercept = (_, range) => range == "bytes=0-0" ? 416 : null;
        _site.Chunked = true;

        var empty = await ResolveAsync(_site.Add("/none.bin", []));
        var some = await ResolveAsync(_site.Add("/some.bin", Bytes(1000)));

        Assert.Equal(0, empty.Size);
        Assert.Null(some.Size);
    }

    [Fact]
    public async Task ASitesSizeIsBelievedOverAServerThatSaysTheFileIsEmpty()
    {
        var url = _site.Add("/sized.bin", []);
        var site = new TestSite((link, _) => Task.FromResult(new ResolvedFile(link, "sized", "sized.bin", url, null, "ferry-test", null, false, Size: 1000)));

        var failure = await Assert.ThrowsAsync<StageException>(() => site.Downloader(_http).RunAsync(new DownloadRequest(site.Link, _folder), _ => { }, CancellationToken.None));

        Assert.True(failure.IsRetryable);
        Assert.Contains("empty", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_folder, "sized.bin")));
    }

    [Fact]
    public async Task ASiteTakingLongToGiveANewLinkIsNoStall()
    {
        var data = Bytes(MiB);
        var url = _site.Add("/slow.bin", data);
        var refused = 0;
        _site.Intercept = (_, range) => range != "bytes=0-0" && Interlocked.Exchange(ref refused, 1) == 0 ? 403 : null;
        var asked = 0;
        var site = new TestSite(async (link, cancel) =>
        {
            if (Interlocked.Increment(ref asked) > 1)
                await Task.Delay(TimeSpan.FromMilliseconds(900), cancel);
            return new ResolvedFile(link, "slow", "slow.bin", url, null, "ferry-test", null, false, data.Length);
        });
        var events = new ConcurrentQueue<DownloadEvent>();

        var result = await site.Downloader(_http, stall: TimeSpan.FromMilliseconds(300)).RunAsync(new DownloadRequest(site.Link, _folder), events.Enqueue, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(result.Path));
        Assert.Single(events.OfType<DownloadLinkRenewed>());
        Assert.DoesNotContain(events.OfType<DownloadRetrying>(), retry => retry.Reason.StartsWith("No data", StringComparison.Ordinal));
    }

    /// <summary>A site whose answers the test gives, for a link of its own.</summary>
    private sealed class TestSite(Func<FileLink, CancellationToken, Task<ResolvedFile>> resolve) : ISite
    {
        public string Id => "test";

        public string Name => "Test";

        public FileLink Link { get; } = new("test", new Uri("https://site.test/file"), "file");

        public bool Claims(Uri link) => link == Link.Original;

        public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
        {
            (file, reason) = (Link, null);
            return true;
        }

        public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel) => resolve(file, cancel);

        public SegmentedDownload Downloader(HttpFetcher http, TimeSpan? stall = null)
        {
            var options = DownloadOptions.For(DownloadProfile.Balanced, connections: 1, ExistingFile.Rename);
            return new SegmentedDownload(http, new SiteCatalog([new SiteEntry(this)]), options with
            {
                StallTimeout = stall ?? options.StallTimeout,
                Retry = RetryPolicy.Untimed(maxRetries: 2, wait: TimeSpan.FromMilliseconds(1)),
            });
        }
    }

    [Fact]
    public async Task TheServersNameWinsOverTheLinks()
    {
        await using var queue = Queue();

        var item = await RunAsync(queue, _site.Add("/get", Bytes(1000), disposition: "attachment; filename=\"report.pdf\""));

        Assert.Equal("report.pdf", Path.GetFileName(item.Path));
    }

    [Fact]
    public async Task ARedirectToAnotherSiteStillDownloads()
    {
        var data = Bytes(2 * MiB);
        await using var queue = Queue();
        var target = _other.Add("/release/asset.zip", data);

        var item = await RunAsync(queue, _site.Redirect("/latest", target));

        Assert.Equal(QueueState.Done, item.State);
        Assert.Equal("asset.zip", Path.GetFileName(item.Path));
        Assert.Equal(data, await File.ReadAllBytesAsync(item.Path!));
    }

    [Fact]
    public async Task CookiesAndHeadersGoToTheLinksSiteAndNotPastARedirectToAnother()
    {
        await using var queue = Queue();
        var target = _other.Add("/asset.zip", Bytes(2 * MiB));
        var link = _site.Redirect("/latest", target);
        var details = new RequestDetails(new Uri("https://from.test/"), Login, new Dictionary<string, string> { ["X-Token"] = "hush" });

        var item = await RunAsync(queue, link, new AddOptions(Details: details));

        Assert.Equal(QueueState.Done, item.State);
        Assert.All(_site.Seen("/latest", "Cookie"), cookie => Assert.Equal(Login, cookie));
        Assert.All(_site.Seen("/latest", "X-Token"), token => Assert.Equal("hush", token));
        Assert.NotEmpty(_other.Seen("/asset.zip", "Cookie"));
        Assert.All(_other.Seen("/asset.zip", "Cookie"), Assert.Null);
        Assert.All(_other.Seen("/asset.zip", "X-Token"), Assert.Null);
        // CDNs check it, so it is the one thing that does go on
        Assert.All(_other.Seen("/asset.zip", "Referer"), referer => Assert.Equal("https://from.test/", referer));
    }

    [Fact]
    public async Task ARedirectWithinTheSiteKeepsTheCookies()
    {
        await using var queue = Queue();
        var target = _site.Add("/real.bin", Bytes(1000));

        await RunAsync(queue, _site.Redirect("/short", target), new AddOptions(Details: new RequestDetails(Cookies: Login)));

        Assert.NotEmpty(_site.Seen("/real.bin", "Cookie"));
        Assert.All(_site.Seen("/real.bin", "Cookie"), cookie => Assert.Equal(Login, cookie));
    }

    [Fact]
    public async Task ACookieARedirectSetsGoesToTheNextHopOfThatRequestAlone()
    {
        _site.SetCookie = "FedAuth=abc; Path=/; HttpOnly";
        await using var queue = Queue();
        var link = _site.Redirect("/guest", _site.Add("/shared.bin", Bytes(1000)));

        var item = await RunAsync(queue, link);

        Assert.Equal(QueueState.Done, item.State);
        Assert.NotEmpty(_site.Seen("/shared.bin", "Cookie"));
        Assert.All(_site.Seen("/shared.bin", "Cookie"), cookie => Assert.Equal("FedAuth=abc", cookie));
        Assert.All(_site.Seen("/guest", "Cookie"), Assert.Null); // every request starts with none: nothing is kept
    }

    [Fact]
    public async Task ACookieARedirectSetsJoinsThoseGivenAndReplacesItsOwnName()
    {
        _site.SetCookie = "seen=new";
        await using var queue = Queue();
        var link = _site.Redirect("/guest", _site.Add("/joined.bin", Bytes(1000)));

        await RunAsync(queue, link, new AddOptions(Details: new RequestDetails(Cookies: "session=secret; seen=old")));

        Assert.NotEmpty(_site.Seen("/joined.bin", "Cookie"));
        Assert.All(_site.Seen("/joined.bin", "Cookie"), cookie => Assert.Equal("session=secret; seen=new", cookie));
    }

    [Fact]
    public async Task ACookieARedirectSetsStaysOnItsSite()
    {
        _site.SetCookie = "FedAuth=abc";
        await using var queue = Queue();
        var back = _site.Add("/back.bin", Bytes(1000));
        var link = _site.Redirect("/out", _other.Redirect("/hop", back));

        var item = await RunAsync(queue, link);

        Assert.Equal(QueueState.Done, item.State);
        Assert.NotEmpty(_other.Seen("/hop", "Cookie"));
        Assert.All(_other.Seen("/hop", "Cookie"), Assert.Null);
        Assert.All(_site.Seen("/back.bin", "Cookie"), cookie => Assert.Equal("FedAuth=abc", cookie)); // its own site again, on the same request
    }

    [Fact]
    public async Task ALinkToAWebPageIsSaved()
    {
        await using var queue = Queue();

        var item = await RunAsync(queue, _site.Add("/page.html", Encoding.UTF8.GetBytes("<html></html>"), "text/html; charset=utf-8"));

        Assert.Equal(QueueState.Done, item.State);
        Assert.Equal("<html></html>", await File.ReadAllTextAsync(item.Path!));
    }

    /// <summary>A part half done, as a pause leaves it, of the file at <paramref name="url"/>.</summary>
    private async Task<(FileLink Link, string Path)> PausedPartAsync(Uri url, string name, long total, byte[] part)
    {
        Assert.True(SiteCatalog.Standard(_http, "ferry-test", []).TryParse(url.ToString(), out var link, out _));
        var path = Path.Combine(_folder, name);
        await File.WriteAllBytesAsync(PartFile.PartPath(path), part);
        PartFile.Save(path, new PartState(link.Key, "", total, null, null, [new SegmentState(0, total - 1, total / 2)]));
        return (link, path);
    }

    /// <summary>The first half of <paramref name="data"/> downloaded, with its state, as a pause leaves it.</summary>
    private async Task<(FileLink Link, string Path)> HalfDoneAsync(Uri url, string name, byte[] data)
    {
        Assert.True(SiteCatalog.Standard(_http, "ferry-test", []).TryParse(url.ToString(), out var link, out _));
        var path = Path.Combine(_folder, name);
        var half = data.Length / 2;
        await File.WriteAllBytesAsync(PartFile.PartPath(path), [.. data.AsSpan(0, half), .. new byte[data.Length - half]]);
        PartFile.Save(path, new PartState(link.Key, link.Key, data.Length, null, null, [new SegmentState(0, data.Length, half)]));
        return (link, path);
    }

    [Fact]
    public async Task AResumedPartSurvivesA416ToTheFirstByte()
    {
        var data = Bytes(2 * MiB);
        var url = _site.Add("/resumed.bin", data);
        var (link, path) = await HalfDoneAsync(url, "resumed.bin", data);
        _site.Intercept = (_, range) => range == "bytes=0-0" ? 416 : null;
        var events = new ConcurrentQueue<DownloadEvent>();

        await Downloader().RunAsync(new DownloadRequest(link, _folder, "resumed.bin"), events.Enqueue, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.Empty(events.OfType<DownloadRestarted>());
        Assert.True(events.OfType<DownloadStarted>().Single().Resumed);
        Assert.DoesNotContain(_site.Seen("/resumed.bin", "Range"), range => range != "bytes=0-0" && range?.StartsWith("bytes=0-", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task AResumedPartSurvivesAServerThatNowSaysTheFileIsEmpty()
    {
        var data = Bytes(2 * MiB);
        var url = _site.Add("/emptied.bin", data);
        var (link, path) = await HalfDoneAsync(url, "emptied.bin", data);
        var part = await File.ReadAllBytesAsync(PartFile.PartPath(path));
        _site.Add("/emptied.bin", []);

        var failure = await Assert.ThrowsAsync<StageException>(() =>
            Downloader().RunAsync(new DownloadRequest(link, _folder, "emptied.bin"), _ => { }, CancellationToken.None));

        Assert.True(failure.IsRetryable);
        Assert.False(File.Exists(path));
        Assert.Equal(part, await File.ReadAllBytesAsync(PartFile.PartPath(path)));
    }

    [Fact]
    public async Task ANewLinkThatGivesAWebPageCostsNeitherThePartNorBecomesTheFile()
    {
        var data = Bytes(2 * MiB);
        var url = _site.Add("/login.bin", data);
        var (link, path) = await HalfDoneAsync(url, "login.bin", data);
        var part = await File.ReadAllBytesAsync(PartFile.PartPath(path));
        _site.Intercept = (_, range) =>
        {
            if (range == "bytes=0-0")
                return null;
            _site.Add("/login.bin", Encoding.UTF8.GetBytes("<html>log in</html>"), "text/html");
            return 403;
        };

        var failure = await Assert.ThrowsAsync<StageException>(() =>
            Downloader().RunAsync(new DownloadRequest(link, _folder, "login.bin"), _ => { }, CancellationToken.None));

        Assert.Contains("web page", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.Equal(part, await File.ReadAllBytesAsync(PartFile.PartPath(path)));
        Assert.True(File.Exists(PartFile.StatePath(path)));
    }

    [Fact]
    public async Task AFileReplacedWhileItDownloadsStartsOverAndCompletes()
    {
        var replaced = Bytes(2 * MiB, seed: 8);
        var url = _site.Add("/moving.bin", Bytes(3 * MiB));
        Assert.True(SiteCatalog.Standard(_http, "ferry-test", []).TryParse(url.ToString(), out var link, out _));
        _site.Intercept = (_, range) =>
        {
            if (range != "bytes=0-0")
                _site.Add("/moving.bin", replaced);
            return null;
        };
        var events = new ConcurrentQueue<DownloadEvent>();

        var result = await Downloader().RunAsync(new DownloadRequest(link, _folder), events.Enqueue, CancellationToken.None);

        Assert.Equal(replaced, await File.ReadAllBytesAsync(result.Path));
        Assert.Single(events.OfType<DownloadRestarted>());
    }

    private SegmentedDownload Downloader() =>
        new(_http, SiteCatalog.Standard(_http, "ferry-test", []), DownloadOptions.For(DownloadProfile.Balanced, connections: 2, ExistingFile.Rename) with
        {
            Retry = RetryPolicy.Untimed(maxRetries: 2, wait: TimeSpan.FromMilliseconds(1)),
        });

    [Fact]
    public async Task AWebPageWhereAPausedFileWasCostsNeitherThePartNorBecomesTheFile()
    {
        var url = _site.Add("/movie.mp4", Encoding.UTF8.GetBytes("<html>log in</html>"), "text/html");
        var part = Bytes(MiB);
        var (link, path) = await PausedPartAsync(url, "movie.mp4", part.Length, part);

        var failure = await Assert.ThrowsAsync<StageException>(() =>
            Downloader().RunAsync(new DownloadRequest(link, _folder, "movie.mp4", null), _ => { }, CancellationToken.None));

        Assert.Contains("web page", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.Equal(part, await File.ReadAllBytesAsync(PartFile.PartPath(path)));
    }

    [Fact]
    public async Task APartCutShortStartsAgainRatherThanLeavingAHole()
    {
        var data = Bytes(2 * MiB);
        var url = _site.Add("/short.bin", data);
        var (link, path) = await PausedPartAsync(url, "short.bin", data.Length, new byte[1024]);

        await Downloader().RunAsync(new DownloadRequest(link, _folder, "short.bin", null), _ => { }, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task SkippingAnExistingFileStillChecksItsChecksum()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, "there.bin"), "not it");
        await using var queue = Queue(o => o with { OnExisting = ExistingFile.Skip });

        var item = await RunAsync(queue, _site.Add("/there.bin", Bytes(MiB)), new AddOptions(Sha256: new string('0', 64)));

        Assert.Equal(QueueState.Failed, item.State);
        Assert.Equal(Stage.Verify, item.FailedAt);
        Assert.Equal("not it", await File.ReadAllTextAsync(Path.Combine(_folder, "there.bin")));
    }

    [Fact]
    public async Task ACompressedFileIsSavedAsTheServerHoldsIt()
    {
        // what a browser would unpack, because the server says it is compressed in transit, is still the file that was asked for
        var seen = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, _site.Add("/a.bin", Bytes(1024))), Stage.Download, CancellationToken.None, asFile: true);

        Assert.Empty(seen.RequestMessage!.Headers.AcceptEncoding);
    }

    [Fact]
    public async Task TheRightChecksumPassesAndAWrongOneFailsAndKeepsTheFile()
    {
        var data = Bytes(1 * MiB);
        var right = Convert.ToHexStringLower(SHA256.HashData(data));
        await using var queue = Queue();

        var good = await RunAsync(queue, _site.Add("/good.bin", data), new AddOptions(Sha256: right));
        var bad = await RunAsync(queue, _site.Add("/bad.bin", data), new AddOptions(Sha256: new string('0', 64)));

        Assert.Equal(QueueState.Done, good.State);
        Assert.Equal(right, good.Sha256);
        Assert.Equal(QueueState.Failed, bad.State);
        Assert.Equal(Stage.Verify, bad.FailedAt);
        Assert.True(Directory.GetFiles(_folder, "bad.bin*").Length > 0);
    }

    [Fact]
    public async Task APlainDownloadClaimsNoChecksumItWasNeverChecked()
    {
        var data = Bytes(MiB);
        await using var plain = Queue();
        await using var always = Queue(options => options with { AlwaysHash = true });

        var item = await RunAsync(plain, _site.Add("/plain.bin", data));
        var hashed = await RunAsync(always, _site.Add("/hashed.bin", data));

        Assert.Equal(QueueState.Done, item.State);
        Assert.Null(item.Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), hashed.Sha256);
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("  ", true, null)]
    [InlineData("zz", false, "zz")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", true, "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    public void AChecksumIsReadOrRefused(string? text, bool read, string? sha256)
    {
        Assert.Equal(read, FileChecksum.TryRead(text, out var value));
        Assert.Equal(sha256, value);
    }

    [Fact]
    public async Task SortingByTypePutsAFileInItsKindsFolderUnlessAFolderWasChosen()
    {
        await using var queue = Queue(options => options with { SortByType = true });
        var chosen = Path.Combine(_folder, "mine");

        var sorted = await RunAsync(queue, _site.Add("/clip.mp4", Bytes(1000)));
        var kept = await RunAsync(queue, _site.Add("/other.mp4", Bytes(1000)), new AddOptions(Folder: chosen));

        Assert.Equal(Path.Combine(_folder, FileKinds.FolderFor(FileKind.Video)!, "clip.mp4"), sorted.Path);
        Assert.Equal(Path.Combine(chosen, "other.mp4"), kept.Path);
    }

    [Fact]
    public async Task AnAlbumGetsAFolderOfItsOwnAndCantLeaveTheDownloadFolder()
    {
        await using var queue = Queue();

        var album = await RunAsync(queue, _site.Add("/a.bin", Bytes(100)), new AddOptions(Album: "Summer 2026"));
        var odd = await RunAsync(queue, _site.Add("/b.bin", Bytes(100)), new AddOptions(Album: ".."));

        Assert.Equal(Path.Combine(_folder, "Summer 2026", "a.bin"), album.Path);
        Assert.Equal(_folder, Path.GetDirectoryName(Path.GetDirectoryName(odd.Path)));
    }

    [Fact]
    public async Task ALinkAlreadyInTheQueueIsNotAddedTwiceHoweverItsFragmentReads()
    {
        await using var queue = Queue();
        var link = _site.Add("/one.bin", Bytes(100));

        await RunAsync(queue, link);
        var again = await queue.AddAsync([$"{link}#part"]);

        Assert.Empty(again.Added);
        Assert.Single(again.Duplicates);
    }

    [Theory]
    [InlineData("23:00", "07:00", "23:30", true)]
    [InlineData("23:00", "07:00", "03:00", true)]
    [InlineData("23:00", "07:00", "07:00", false)]
    [InlineData("23:00", "07:00", "12:00", false)]
    [InlineData("09:00", "17:00", "09:00", true)]
    [InlineData("09:00", "17:00", "17:00", false)]
    [InlineData("08:00", "08:00", "20:00", true)]
    public void TheScheduleRunsAcrossMidnight(string start, string stop, string now, bool open)
    {
        var schedule = new DownloadSchedule(TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
                                            TimeOnly.Parse(stop, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(open, schedule.IsOpen(TimeOnly.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task WhileHeldNothingStartsAndLettingGoRunsIt()
    {
        await using var queue = Queue();
        await queue.SetHeldAsync(true);

        var id = Assert.Single((await queue.AddAsync([_site.Add("/later.bin", Bytes(1000)).ToString()])).Added).Id;

        Assert.Equal(QueueState.Waiting, queue.Find(id)!.State);
        await queue.SetHeldAsync(false);
        await QueueWait.UntilAsync(queue, () => queue.Find(id)!.State == QueueState.Done);
    }

    [Fact]
    public void RequestDetailsSurviveARestartAndNeverShowInText()
    {
        var details = new RequestDetails(new Uri("https://from.test/"), Login, new Dictionary<string, string> { ["X-Token"] = "hush" });
        var id = Guid.NewGuid();
        var secrets = new SecretStore(Path.Combine(_folder, "secrets"));

        new RequestStore(secrets).Save(new Dictionary<Guid, RequestDetails> { [id] = details });
        var read = new RequestStore(secrets).Load()[id];

        Assert.Equal(details.Referer, read.Referer);
        Assert.Equal(Login, read.Cookies);
        Assert.Equal("hush", read.Headers!["X-Token"]);
        Assert.DoesNotContain("secret", details.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hush", read.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailsComeBackWithTheirFileFromARestart()
    {
        var link = _site.Add("/behind-login.bin", Bytes(1000));
        var saved = new SavedQueueItem(Guid.NewGuid(), link.ToString(), _folder, null, QueueState.Waiting, null, 0, null, 0, null, null);
        await using var queue = Queue();

        queue.Restore([saved], new Dictionary<Guid, RequestDetails> { [saved.Id] = new(Cookies: Login) });
        Assert.Equal(QueueState.Paused, queue.Find(saved.Id)!.State); // nothing starts without being asked
        await queue.ResumeAsync(saved.Id);
        await QueueWait.UntilAsync(queue, () => queue.Find(saved.Id)!.State == QueueState.Done);

        Assert.All(_site.Seen("/behind-login.bin", "Cookie"), cookie => Assert.Equal(Login, cookie));
        Assert.Equal(saved.Id, Assert.Single(queue.Items).Id);
        Assert.Empty(queue.RequestsToKeep()); // done: the login is dropped
    }
}
