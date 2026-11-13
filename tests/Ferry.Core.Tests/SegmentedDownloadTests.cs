using System.Collections.Concurrent;
using System.Security.Cryptography;
using Ferry.Core;
using Ferry.Core.Download;
using Ferry.Core.Http;

namespace Ferry.Core.Tests;

/// <summary>The downloader against <see cref="FakeSite"/>'s CDN: parts, resume, retries, new links, names.</summary>
public sealed class SegmentedDownloadTests : IDisposable
{
    private const int MiB = 1 << 20;
    private const string Name = "clip.mp4";
    private readonly FakeSite _site = new();
    private readonly HttpFetcher _http = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("ferry_download_").FullName;
    private readonly ConcurrentQueue<DownloadEvent> _events = new();
    private Action<DownloadEvent>? _watch;

    public void Dispose()
    {
        _http.Dispose();
        _site.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static DownloadOptions Options(int connections = 4, ExistingFile onExisting = ExistingFile.Rename) =>
        DownloadOptions.For(DownloadProfile.Balanced, connections, onExisting) with
        {
            StallTimeout = TimeSpan.FromMilliseconds(300),
            Retry = RetryPolicy.Untimed(maxRetries: 3, wait: TimeSpan.FromMilliseconds(1)),
        };

    private Task<DownloadResult> DownloadAsync(DownloadOptions? options = null, FakeSiteOptions? fake = null, string? name = null,
                                               SpeedLimit? limit = null, CancellationToken cancel = default) =>
        Downloader(options, fake).RunAsync(Request("clip", name, limit), Record, cancel);

    private SegmentedDownload Downloader(DownloadOptions? options = null, FakeSiteOptions? fake = null, SpeedLimit? overall = null)
    {
        var site = fake ?? _site.Options();
        return new SegmentedDownload(_http, TestSites.Only(_http, site), options ?? Options(), overall);
    }

    private DownloadRequest Request(string slug, string? name = null, SpeedLimit? limit = null) =>
        new(TestSites.Link($"https://files.test/f/{slug}"), _folder, name, limit);

    private void Record(DownloadEvent e)
    {
        _events.Enqueue(e);
        _watch?.Invoke(e);
    }

    private CdnFile AddFile(int size, int seed = 1, string slug = "clip", string id = "42", string name = Name)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return _site.AddFile(slug, id, name, data);
    }

    private string Saved(string name = Name) => Path.Combine(_folder, name);

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private string[] FolderFiles => [.. Directory.GetFiles(_folder).Select(Path.GetFileName).OfType<string>().Order()];

    /// <summary>Start a download, and cancel it once the bytes before <paramref name="holdAt"/> are on disk.</summary>
    private async Task StartAndCancelAsync(CdnFile file, long holdAt, long doneAtLeast)
    {
        file.HoldAt = holdAt;
        using var cancel = new CancellationTokenSource();
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch = e =>
        {
            if (e is DownloadProgressed p && p.Done >= doneAtLeast)
                enough.TrySetResult();
        };
        var running = DownloadAsync(cancel: cancel.Token);
        await Task.WhenAny(enough.Task, running);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        _watch = null;
        file.HoldAt = null;
        file.Release();
    }

    [Fact]
    public async Task FileIsDownloadedOverSeveralConnections()
    {
        var file = AddFile(8 * MiB);

        var result = await DownloadAsync(Options() with { AlwaysHash = true });

        // the hash is made as the parts land, and still is the whole file's
        Assert.Equal(new DownloadResult(Saved(), Skipped: false, Sha256(file.Data)), result);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal([Name], FolderFiles);
        var started = _events.OfType<DownloadStarted>().Single();
        Assert.Equal((8L * MiB, 4, false), (started.Total!.Value, started.Connections, started.Resumed));
        Assert.True(file.Ranges.Count(r => r != "bytes=0-0") >= 4);
        Assert.Equal(new DownloadFinished(Saved(), 8 * MiB), _events.OfType<DownloadFinished>().Single());
        Assert.Equal(8 * MiB, _events.OfType<DownloadProgressed>().Last().Done);
        var referer = _site.Requests.First(r => r.Request.StartsWith("GET /cdn/", StringComparison.Ordinal)).Headers["Referer"];
        Assert.Equal("https://referer.test/file/42", referer);
    }

    [Fact]
    public async Task ServerIgnoringRangesGetsOneConnection()
    {
        var file = AddFile(3 * MiB);
        file.IgnoreRange = true;

        await DownloadAsync();

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal(1, _events.OfType<DownloadStarted>().Single().Connections);
        Assert.Equal(2, file.Ranges.Count); // the probe, then the file
    }

    [Fact]
    public async Task ServerThatStopsSendingPartsGetsOneConnectionInstead()
    {
        var file = AddFile(3 * MiB);
        file.RangesForProbeOnly = true;

        var result = await DownloadAsync(Options() with { AlwaysHash = true });

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal(Sha256(file.Data), result.Sha256);
        Assert.Equal([Name], FolderFiles);
        Assert.Contains(_events.OfType<DownloadRestarted>(), r => r.Reason.Contains("one connection", StringComparison.Ordinal));
        Assert.Equal(1, _events.OfType<DownloadStarted>().Last().Connections);
    }

    [Fact]
    public async Task EmptyFileIsSavedEmpty()
    {
        AddFile(0);

        var result = await DownloadAsync(Options() with { AlwaysHash = true });

        Assert.Equal(new DownloadResult(Saved(), Skipped: false, Sha256([])), result);
        Assert.Equal(0, new FileInfo(Saved()).Length);
        Assert.Equal([Name], FolderFiles);
        Assert.Equal(new DownloadFinished(Saved(), 0), _events.OfType<DownloadFinished>().Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADownloadNothingNeedsTheHashOfHashesNothing(bool oneConnection)
    {
        var file = AddFile(3 * MiB);
        file.IgnoreRange = oneConnection;

        var result = await DownloadAsync();

        Assert.Equal(new DownloadResult(Saved(), Skipped: false), result);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
    }

    [Fact]
    public async Task APartSurvivesA416ToItsFirstByte()
    {
        var file = AddFile(8 * MiB);
        await StartAndCancelAsync(file, holdAt: 5 * MiB, doneAtLeast: 4 * MiB);
        file.RefuseFirstByte = true;
        _events.Clear();

        await DownloadAsync();

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.True(_events.OfType<DownloadStarted>().Single().Resumed);
        Assert.Empty(_events.OfType<DownloadRestarted>());
    }

    [Fact]
    public async Task APartSurvivesAServerThatNowSaysTheFileIsEmpty()
    {
        var file = AddFile(8 * MiB);
        await StartAndCancelAsync(file, holdAt: 5 * MiB, doneAtLeast: 4 * MiB);
        var data = file.Data;
        file.Data = [];
        _events.Clear();

        var error = await Assert.ThrowsAsync<StageException>(() => DownloadAsync());

        Assert.True(error.IsRetryable);
        Assert.Contains("is kept", error.Message, StringComparison.Ordinal);
        Assert.Equal(["clip.mp4.ferry-part", "clip.mp4.ferry-part.json"], FolderFiles);
        Assert.Empty(_events.OfType<DownloadRestarted>());

        file.Data = data; // it was only saying so
        await DownloadAsync();

        Assert.Equal(data, await File.ReadAllBytesAsync(Saved()));
        Assert.True(_events.OfType<DownloadStarted>().Single().Resumed);
    }

    [Fact]
    public async Task ANewCapIsForNewTurnsAndTurnsAlreadyTakenStillEnd()
    {
        var hosts = new HostConnections(1);
        Assert.Same(hosts, hosts.WithCap(1));
        var taken = await hosts.WaitAsync("cdn.test", CancellationToken.None);

        var wider = hosts.WithCap(2);

        Assert.NotSame(hosts, wider);
        using (await wider.WaitAsync("cdn.test", CancellationToken.None))
        using (await wider.WaitAsync("cdn.test", CancellationToken.None))
        {
            taken.Dispose();
            using var freed = await hosts.WaitAsync("cdn.test", CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CancelledDownloadContinuesWhereItStopped()
    {
        var file = AddFile(8 * MiB);
        await StartAndCancelAsync(file, holdAt: 5 * MiB, doneAtLeast: 4 * MiB);
        Assert.Equal(["clip.mp4.ferry-part", "clip.mp4.ferry-part.json"], FolderFiles);
        _events.Clear();
        var requestsBefore = file.Ranges.Count;

        // a new downloader, as after a restart: only the files on disk carry over
        var result = await DownloadAsync(Options() with { AlwaysHash = true });

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal(Sha256(file.Data), result.Sha256); // what was there already was hashed first
        Assert.Equal([Name], FolderFiles);
        var started = _events.OfType<DownloadStarted>().Single();
        Assert.True(started.Resumed);
        Assert.True(started.Done >= 4 * MiB);
        var resumedRanges = file.Ranges.Skip(requestsBefore).Where(r => r != "bytes=0-0").ToList();
        Assert.NotEmpty(resumedRanges);
        Assert.DoesNotContain(resumedRanges, r => r is not null && (r.StartsWith("bytes=0-", StringComparison.Ordinal)
                                                                   || r.StartsWith($"bytes={2 * MiB}-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TwoFilesOfOneNameStartingTogetherGetTwoNames()
    {
        var a = AddFile(3 * MiB, seed: 1, slug: "a", id: "1", name: "a.mp4");
        var b = AddFile(3 * MiB, seed: 2, slug: "b", id: "2", name: "b.mp4");
        a.ChunkDelay = b.ChunkDelay = TimeSpan.FromMilliseconds(2);

        // both saved as clip.mp4 (as two album files of one name would be)
        var results = await Task.WhenAll(Downloader().RunAsync(Request("a", Name), Record, CancellationToken.None),
                                         Downloader().RunAsync(Request("b", Name), Record, CancellationToken.None));

        Assert.Equal(["clip (1).mp4", Name], [.. results.Select(r => Path.GetFileName(r.Path)).Order(StringComparer.Ordinal)]);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(results[0].Path));
        Assert.Equal(b.Data, await File.ReadAllBytesAsync(results[1].Path));
    }

    [Fact]
    public async Task ResumedDownloadWhoseNameWasTakenFinishesUnderTheNextOne()
    {
        var file = AddFile(8 * MiB);
        await StartAndCancelAsync(file, holdAt: 5 * MiB, doneAtLeast: 4 * MiB);
        await File.WriteAllTextAsync(Saved(), "someone else's");

        var result = await DownloadAsync();

        Assert.Equal(Saved("clip (1).mp4"), result.Path);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(result.Path));
        Assert.Equal("someone else's", await File.ReadAllTextAsync(Saved()));
        Assert.True(_events.OfType<DownloadStarted>().Last().Resumed);
    }

    [Fact]
    public async Task PartWithADamagedStateStartsOverUnderItsName()
    {
        var file = AddFile(3 * MiB);
        await File.WriteAllBytesAsync(Saved() + ".ferry-part", new byte[1000]);
        await File.WriteAllTextAsync(Saved() + ".ferry-part.json", "{\"slug\":\"cl"); // cut short by a crash

        var result = await DownloadAsync();

        Assert.Equal(Saved(), result.Path);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal([Name], FolderFiles);
    }

    [Fact]
    public async Task ChangedFileStartsOver()
    {
        var file = AddFile(8 * MiB);
        await StartAndCancelAsync(file, holdAt: 5 * MiB, doneAtLeast: 4 * MiB);
        var changed = new byte[6 * MiB];
        new Random(2).NextBytes(changed);
        (file.Data, file.ETag) = (changed, "\"v2\"");
        _events.Clear();

        await DownloadAsync();

        Assert.Equal(changed, await File.ReadAllBytesAsync(Saved()));
        Assert.Single(_events.OfType<DownloadRestarted>());
        Assert.False(_events.OfType<DownloadStarted>().Single().Resumed);
        Assert.Equal([Name], FolderFiles);
    }

    [Fact]
    public async Task DroppedConnectionIsRetried()
    {
        var file = AddFile(8 * MiB);
        file.DropOnceAt = 3 * MiB + 100;

        await DownloadAsync();

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.NotEmpty(_events.OfType<DownloadRetrying>());
    }

    [Fact]
    public async Task BusyServerGetsFewerConnections()
    {
        var file = AddFile(8 * MiB);
        file.ChunkDelay = TimeSpan.FromMilliseconds(5);
        file.BusyAbove = 2;

        await DownloadAsync();

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        var reduced = _events.OfType<DownloadConnectionsReduced>().ToList();
        Assert.NotEmpty(reduced);
        Assert.All(reduced, r => Assert.Contains("503", r.Reason, StringComparison.Ordinal));
        Assert.Equal(reduced.Count, reduced.Select(r => r.Left).Distinct().Count()); // each stand-down leaves one fewer
    }

    [Fact]
    public async Task ServerThatTakesOneConnectionGetsOne()
    {
        var file = AddFile(8 * MiB);
        file.ChunkDelay = TimeSpan.FromMilliseconds(2);
        file.BusyAbove = 1;

        await DownloadAsync();

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal(1, _events.OfType<DownloadConnectionsReduced>().Last().Left);
    }

    [Fact]
    public async Task StalledConnectionIsRetried()
    {
        var file = AddFile(8 * MiB);
        file.StallOnceAt = 3 * MiB;

        await DownloadAsync();

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.Contains(_events.OfType<DownloadRetrying>(), r => r.Reason.StartsWith("No data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiredLinkIsReplacedAndTheDownloadCarriesOn()
    {
        var file = AddFile(8 * MiB);
        file.ExpireTokensAt = 3 * MiB;

        await DownloadAsync();

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.Single(_events.OfType<DownloadLinkRenewed>());
        Assert.Equal(2, _site.TokensIssued);
        Assert.Equal(2, _site.Count("/f/clip"));
    }

    [Fact]
    public async Task LinkRefusedEvenWhenNewFailsTheDownload()
    {
        AddFile(MiB);
        _site.RefuseAllTokens = true;

        var error = await Assert.ThrowsAsync<StageException>(() => DownloadAsync());

        Assert.Equal(Stage.Download, error.Stage);
        Assert.Contains("even with a new download link", error.Message, StringComparison.Ordinal);
        Assert.Empty(FolderFiles);
    }

    [Fact]
    public async Task WebPageInsteadOfTheFileFails()
    {
        AddFile(MiB).ContentType = "text/html";

        var error = await Assert.ThrowsAsync<StageException>(() => DownloadAsync());

        Assert.Contains("web page", error.Message, StringComparison.Ordinal);
        Assert.Empty(FolderFiles);
    }

    [Fact]
    public async Task SlowDownloadIsNotCutShortByTheStepTimeouts()
    {
        var file = AddFile(MiB);
        file.ChunkDelay = TimeSpan.FromMilliseconds(50); // 16 chunks: about 0.8 s, four times the step timeout
        // short step timeouts, retried so a busy test run doesn't fail the page; every one is far below the download's time
        var quick = new RetryPolicy(3, TimeSpan.Zero, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400));

        await DownloadAsync(Options(connections: 1), fake: _site.Options() with { PageRetry = quick, ApiRetry = quick });

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
    }

    [Fact]
    public async Task ExistingFileIsKeptAndTheNewOneRenamed()
    {
        var file = AddFile(MiB);
        await File.WriteAllTextAsync(Saved(), "old");

        var result = await DownloadAsync();

        Assert.Equal(Saved("clip (1).mp4"), result.Path);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(result.Path));
        Assert.Equal("old", await File.ReadAllTextAsync(Saved()));
    }

    [Fact]
    public async Task ExistingFileCanBeSkipped()
    {
        var file = AddFile(MiB);
        await File.WriteAllTextAsync(Saved(), "old");

        var result = await DownloadAsync(Options(onExisting: ExistingFile.Skip));

        Assert.Equal(new DownloadResult(Saved(), Skipped: true), result);
        Assert.Empty(file.Ranges);
        Assert.Equal("old", await File.ReadAllTextAsync(Saved()));
    }

    [Fact]
    public async Task ExistingFileCanBeOverwritten()
    {
        var file = AddFile(MiB);
        await File.WriteAllTextAsync(Saved(), "old");

        var result = await DownloadAsync(Options(onExisting: ExistingFile.Overwrite));

        Assert.Equal(Saved(), result.Path);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal([Name], FolderFiles);
    }

    [Fact]
    public async Task ChosenNameIsMadeSafe()
    {
        AddFile(MiB);

        var result = await DownloadAsync(name: "my: clip?.mp4");

        Assert.Equal(Saved("my_ clip_.mp4"), result.Path);
    }

    [Fact]
    public async Task FileLimitHoldsTheDownloadToItsRate()
    {
        var file = AddFile(2 * MiB);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await DownloadAsync(limit: new SpeedLimit(2 * MiB));

        // 2 MiB at 2 MiB/s, less the quarter-second burst: at least 0.75 s
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.7, 10);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
    }

    [Fact]
    public async Task LimitChangedWhileDownloadingTakesEffect()
    {
        var file = AddFile(4 * MiB);
        var limit = new SpeedLimit(128 << 10); // about 30 s for the whole file
        _watch = e =>
        {
            if (e is DownloadProgressed { Done: > 0 })
                limit.BytesPerSecond = SpeedLimit.Unlimited;
        };

        await DownloadAsync(limit: limit).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved()));
    }

    [Fact]
    public async Task OverallLimitIsSharedByEveryDownload()
    {
        var first = AddFile(MiB);
        var second = AddFile(MiB, seed: 2, slug: "other", id: "43", name: "other.mp4");
        var download = Downloader(overall: new SpeedLimit(MiB));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Task.WhenAll(download.RunAsync(Request("clip"), Record, CancellationToken.None),
                           download.RunAsync(Request("other"), Record, CancellationToken.None));

        // 2 MiB together at 1 MiB/s, less the burst: at least 1.75 s
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.6, 20);
        Assert.Equal(first.Data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal(second.Data, await File.ReadAllBytesAsync(Saved("other.mp4")));
    }
}
