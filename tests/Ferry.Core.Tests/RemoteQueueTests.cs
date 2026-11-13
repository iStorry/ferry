using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.Official;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>
/// Server mode without SSH: a real <see cref="AgentHost"/> downloading from <see cref="FakeSite"/>, the PC's
/// <see cref="RemoteQueue"/> talking to it over loopback sockets, and <see cref="LocalFiles"/> in place of the agent's reads over ssh.
/// </summary>
public sealed class RemoteQueueTests : IAsyncLifetime, IDisposable
{
    private const int MiB = 1 << 20;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private readonly FakeSite _site = new();
    private readonly string _root = Directory.CreateTempSubdirectory("ferry_remote_").FullName;
    private readonly LocalFiles _files = new();
    private readonly ConcurrentQueue<QueueEvent> _events = new();
    private readonly List<IAsyncDisposable> _running = [];
    private readonly List<IDisposable> _sockets = [];
    private readonly CancellationTokenSource _stop = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var running in Enumerable.Reverse(_running))
            await running.DisposeAsync().AsTask().WaitAsync(Patience);
    }

    public void Dispose()
    {
        foreach (var socket in _sockets)
            socket.Dispose();
        _stop.Dispose();
        _site.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private AgentPaths Agent => new(Path.Combine(_root, "agent"));

    private string ServerFolder => Path.Combine(_root, "server");

    private string PcFolder => Path.Combine(_root, "pc");

    private string[] ServerFiles => Directory.Exists(ServerFolder)
        ? [.. Directory.GetFiles(ServerFolder).Select(Path.GetFileName).OfType<string>().Order()]
        : [];

    private FerrySettings Settings(params (string Key, string Value)[] extra)
    {
        var file = new Dictionary<string, string>
        {
            ["FERRY_MODE"] = "server",
            ["FERRY_PLUGIN_SERVER"] = "true",
            ["FERRY_FAKE_URL"] = _site.Url.ToString(),
            ["FERRY_DOWNLOAD_RETRY_WAIT"] = "1ms",
            ["FERRY_DOWNLOAD_STALL_TIMEOUT"] = "5s",
            ["FERRY_DOWNLOAD_THREADS"] = "2",
            ["FERRY_DOWNLOAD_DIR"] = PcFolder,
            ["FERRY_SERVER_FOLDER"] = ServerFolder,
        };
        foreach (var (key, value) in extra)
            file[key] = value;
        var settings = FerrySettings.Build(new AppPaths(Path.Combine(_root, "config"), PcFolder), file, new Dictionary<string, string>());
        Assert.Empty(settings.Problems);
        return settings;
    }

    private AgentHost StartAgent(string? version = null)
    {
        var host = AgentHost.Start(Agent, version);
        _running.Add(host);
        return host;
    }

    private RemoteQueue Pc(FerrySettings? settings = null, TimeSpan? heartbeat = null)
    {
        var queue = heartbeat is { } every
            ? new RemoteQueue(settings ?? Settings(), _files) { HeartbeatEvery = every, HeartbeatTimeout = every }
            : new RemoteQueue(settings ?? Settings(), _files);
        queue.Changed += _events.Enqueue;
        _running.Add(queue);
        return queue;
    }

    /// <summary>Connect <paramref name="pc"/> to <paramref name="host"/> over a fresh loopback socket.</summary>
    private async Task AttachAsync(RemoteQueue pc, AgentHost host)
    {
        var (pcEnd, agentEnd) = await ConnectedPairAsync();
        _ = host.ServeAsync(agentEnd, agentEnd, _stop.Token);
        await pc.AttachAsync(pcEnd, pcEnd);
    }

    /// <summary>Connect <paramref name="pc"/> to <paramref name="host"/> through a route that can go quiet.</summary>
    private async Task<Route> AttachThroughRouteAsync(RemoteQueue pc, AgentHost host)
    {
        var (pcEnd, nearEnd) = await ConnectedPairAsync();
        var (farEnd, agentEnd) = await ConnectedPairAsync();
        _ = host.ServeAsync(agentEnd, agentEnd, _stop.Token);
        var route = new Route();
        _ = route.CarryAsync(nearEnd, farEnd);
        _ = route.CarryAsync(farEnd, nearEnd);
        await pc.AttachAsync(pcEnd, pcEnd);
        return route;
    }

    /// <summary>Bytes between the PC and the agent; once quiet, they are lost on the way and nothing closes, as on a
    /// connection that died without saying so.</summary>
    private sealed class Route
    {
        public volatile bool Quiet;

        public async Task CarryAsync(Stream from, Stream to)
        {
            var buffer = new byte[1 << 16];
            try
            {
                int read;
                while ((read = await from.ReadAsync(buffer)) > 0)
                {
                    if (!Quiet)
                        await to.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // a socket closed at the end of the test
            }
        }
    }

    private async Task<(Stream Pc, Stream Agent)> ConnectedPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new TcpClient();
            var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await listener.AcceptTcpClientAsync();
            await connecting;
            _sockets.Add(client);
            _sockets.Add(server);
            return (client.GetStream(), server.GetStream());
        }
        finally
        {
            listener.Stop();
        }
    }

    private CdnFile AddFile(string slug, int size = 2 * MiB, int seed = 1, long? holdAt = null)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        var file = _site.AddFile(slug, seed.ToString(CultureInfo.InvariantCulture), $"{slug}.mp4", data);
        file.HoldAt = holdAt;
        return file;
    }

    private static string Link(string slug, string host = "files.test") => $"https://{host}/f/{slug}";

    private static async Task<Guid> AddOneAsync(RemoteQueue queue, string slug) =>
        Assert.Single((await queue.AddAsync([Link(slug)])).Added).Id;

    private List<QueueState> StatesOf(Guid id) =>
        [.. _events.OfType<QueueItemStateChanged>().Where(e => e.Item.Id == id).Select(e => e.Item.State)];

    [Fact]
    public async Task WhatWasChosenWhenAddingReachesTheServer()
    {
        AddFile("a", seed: 1);
        AddFile("b", seed: 2);
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);

        var paused = Assert.Single((await pc.AddAsync([Link("a")], new AddOptions(Paused: true))).Added);
        Assert.Equal(QueueState.Paused, paused.State);

        var details = new RequestDetails(Cookies: "session=secret");
        var added = Assert.Single((await pc.AddAsync([Link("b")], new AddOptions(Name: "named.mp4", Details: details, Album: "Summer"))).Added);
        await QueueWait.UntilAsync(pc, () => pc.Find(added.Id)!.State == QueueState.Done);

        Assert.Equal(Path.Combine(PcFolder, "Summer", "named.mp4"), pc.Find(added.Id)!.Path);
        Assert.Equal("session=secret", _site.Requests.First(r => r.Request.Contains("/f/b", StringComparison.Ordinal)).Headers["Cookie"]);
        Assert.All(_site.Requests.Where(r => r.Request.Contains("/cdn/", StringComparison.Ordinal)), r => Assert.Null(r.Headers["Cookie"]));
    }

    [Fact]
    public async Task FilesDownloadOnTheServerThenComeOver()
    {
        var (a, b) = (AddFile("a", seed: 1), AddFile("b", seed: 2));
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);

        var added = (await pc.AddAsync([Link("a"), Link("b")])).Added;
        await pc.WhenIdle.WaitAsync(Patience);

        Assert.All(pc.Items, i => Assert.Equal(QueueState.Done, i.State));
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
        Assert.Equal(b.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "b.mp4")));
        Assert.Equal(Path.Combine(PcFolder, "a.mp4"), pc.Find(added[0].Id)!.Path);
        await QueueWait.UntilAsync(host.Queue, () => host.Queue.Items.Count == 0);
        // deleted once brought over and checked: just after the item leaves the queue
        for (var tries = 0; ServerFiles.Length > 0 && tries < 200; tries++)
            await Task.Delay(25);
        Assert.Empty(ServerFiles);
        var states = StatesOf(added[0].Id);
        Assert.Contains(QueueState.Downloading, states);
        Assert.Equal([QueueState.OnServer, QueueState.Transferring, QueueState.Done], states.TakeLast(3));
    }

    [Fact]
    public async Task FilesCanBeKeptOnTheServer()
    {
        var a = AddFile("a");
        var host = StartAgent();
        var pc = Pc(Settings(("FERRY_SERVER_KEEP_FILES", "true")));
        await AttachAsync(pc, host);

        await AddOneAsync(pc, "a");
        await pc.WhenIdle.WaitAsync(Patience);
        await QueueWait.UntilAsync(host.Queue, () => host.Queue.Items.Count == 0);

        Assert.Equal(["a.mp4"], ServerFiles);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
    }

    [Fact]
    public async Task ServerCarriesOnWhileThePcIsAway()
    {
        var a = AddFile("a", holdAt: MiB);
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);
        var id = await AddOneAsync(pc, "a");
        await a.Held.Task.WaitAsync(Patience);

        await pc.DetachAsync();
        a.Release();
        await QueueWait.UntilAsync(host.Queue, () => host.Queue.Find(id)?.State == QueueState.Done);

        Assert.False(pc.IsAttached);
        Assert.NotEqual(QueueState.Done, pc.Find(id)!.State);
        Assert.False(pc.WhenIdle.IsCompleted);
        await AttachAsync(pc, host);
        await pc.WhenIdle.WaitAsync(Patience);
        Assert.Equal(QueueState.Done, pc.Find(id)!.State);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
    }

    [Fact]
    public async Task ANewPcSessionCollectsWhatFinished()
    {
        var a = AddFile("a");
        var host = StartAgent();
        var first = Pc();
        await AttachAsync(first, host);
        var id = await AddOneAsync(first, "a");
        await first.DetachAsync();
        await QueueWait.UntilAsync(host.Queue, () => host.Queue.Find(id)?.State == QueueState.Done);

        var second = Pc(); // the app opened again
        await AttachAsync(second, host);
        await second.WhenIdle.WaitAsync(Patience);

        Assert.Equal(QueueState.Done, second.Find(id)!.State);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
    }

    [Fact]
    public async Task PauseAndResumeReachTheServer()
    {
        var a = AddFile("a", holdAt: MiB);
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);
        var id = await AddOneAsync(pc, "a");
        await a.Held.Task.WaitAsync(Patience);

        await pc.PauseAsync(id);

        Assert.Equal(QueueState.Paused, host.Queue.Find(id)!.State);
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.Paused);
        a.HoldAt = null;
        a.Release();
        await pc.ResumeAsync(id);
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.Done);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
    }

    [Fact]
    public async Task TransferPausesAtOnceAndContinuesWhereItStopped()
    {
        var file = AddFile("a", size: 3 * MiB);
        _files.HoldAt = MiB;
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);
        var id = await AddOneAsync(pc, "a");
        await _files.Held.Task.WaitAsync(Patience);

        await pc.PauseAsync(id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(QueueState.Paused, pc.Find(id)!.State);
        Assert.Null(pc.Find(id)!.TimeLeft);
        _files.HoldAt = null;
        _files.Release();
        await pc.ResumeAsync(id);
        await pc.WhenIdle.WaitAsync(Patience);
        Assert.Equal(QueueState.Done, pc.Find(id)!.State);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
        Assert.Equal(MiB, _files.Opened.Last()); // it continued, not started over
    }

    [Fact]
    public async Task TenLinksAtOnceSomeBrokenLeaveTheAgentWorking()
    {
        // as a user pastes them: good files, pages that are gone, a busy API, a sign service that fails, a web page
        for (var i = 0; i < 5; i++)
            AddFile($"good{i}", size: MiB, seed: 10 + i);
        AddFile("busyapi", size: MiB, seed: 20);
        _site.Fail("/api", HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests);
        AddFile("webpage", size: MiB, seed: 21).ContentType = "text/html";
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);
        string[] links = [.. Enumerable.Range(0, 5).Select(i => Link($"good{i}")), Link("gone1"), Link("gone2"), Link("busyapi"),
                          Link("webpage"), Link("gone3")];

        var added = await pc.AddAsync(links).WaitAsync(Patience);
        await pc.WhenIdle.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(10, added.Added.Count);
        Assert.All(pc.Items, i => Assert.Contains(i.State, new[] { QueueState.Done, QueueState.Failed }));
        Assert.True(pc.Items.Count(i => i.State == QueueState.Done) >= 5); // the good ones (the busy API recovers too)
        // the agent still answers, and takes more
        Assert.NotNull(await pc.FreeSpaceAsync().WaitAsync(Patience));
        AddFile("after", size: MiB, seed: 30);
        await AddOneAsync(pc, "after").WaitAsync(Patience);
        await pc.WhenIdle.WaitAsync(Patience);
        Assert.Equal(QueueState.Done, pc.Items.Single(i => i.Link.Key == "after").State);
    }

    [Fact]
    public async Task StuckDownloadPausedThenABrokenLinkThenAGoodOne()
    {
        // a download that stops at 4%, paused; then a link that is gone; then one that works
        var stuck = AddFile("stuck", size: 4 * MiB, seed: 1, holdAt: 160 << 10);
        AddFile("good", seed: 2);
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);
        var stuckId = await AddOneAsync(pc, "stuck");
        await stuck.Held.Task.WaitAsync(Patience);

        await pc.PauseAsync(stuckId).WaitAsync(TimeSpan.FromSeconds(5));
        var goneId = await AddOneAsync(pc, "gone").WaitAsync(Patience);
        await QueueWait.UntilAsync(pc, () => pc.Find(goneId)?.State == QueueState.Failed);
        var goodId = await AddOneAsync(pc, "good").WaitAsync(Patience);
        await QueueWait.UntilAsync(pc, () => pc.Find(goodId)?.State == QueueState.Done);

        Assert.Equal(QueueState.Paused, pc.Find(stuckId)!.State);
        Assert.Equal(QueueState.Paused, host.Queue.Find(stuckId)!.State);
        await pc.WhenIdle.WaitAsync(Patience);
        stuck.HoldAt = null;
        stuck.Release();
        await pc.ResumeAsync(stuckId).WaitAsync(Patience);
        await QueueWait.UntilAsync(pc, () => pc.Find(stuckId)?.State == QueueState.Done);
        Assert.Equal(stuck.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "stuck.mp4")));
    }

    [Fact]
    public async Task APageThatNeverAnswersFailsAndLeavesTheRestWorking()
    {
        _site.Hangs["/f/hangs"] = true;
        AddFile("good", seed: 2);
        var host = StartAgent();
        var pc = Pc(Settings(("FERRY_FAKE_PAGE_TIMEOUT", "1s"), ("FERRY_FAKE_PAGE_SLOW_TIMEOUT", "2s"), ("FERRY_FILES_AT_ONCE", "1")));
        await AttachAsync(pc, host);

        var hangsId = await AddOneAsync(pc, "hangs");
        var goodId = await AddOneAsync(pc, "good");
        await QueueWait.UntilAsync(pc, () => pc.Find(goodId)?.State == QueueState.Done);

        var failed = pc.Find(hangsId)!;
        Assert.Equal(QueueState.Failed, failed.State);
        Assert.Equal(Stage.Resolve, failed.FailedAt);
        Assert.NotNull(await pc.FreeSpaceAsync().WaitAsync(Patience));
    }

    [Fact]
    public async Task ALinkStillResolvingPausesAtOnce()
    {
        _site.Hangs["/f/hangs"] = true;
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);
        var id = await AddOneAsync(pc, "hangs");
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.Resolving);

        await pc.PauseAsync(id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(QueueState.Paused, host.Queue.Find(id)!.State);
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.Paused);
        await pc.WhenIdle.WaitAsync(Patience);
    }

    [Fact]
    public async Task PausedAndFailedStayThatWayAcrossAReconnectAndARestart()
    {
        var stuck = AddFile("stuck", size: 4 * MiB, seed: 1, holdAt: 160 << 10);
        var first = AgentHost.Start(Agent);
        var pc = Pc();
        await AttachAsync(pc, first);
        var stuckId = await AddOneAsync(pc, "stuck");
        await stuck.Held.Task.WaitAsync(Patience);
        await pc.PauseAsync(stuckId).WaitAsync(Patience);
        var goneId = await AddOneAsync(pc, "gone");
        await QueueWait.UntilAsync(pc, () => pc.Find(goneId)?.State == QueueState.Failed);

        await pc.DetachAsync();
        await AttachAsync(pc, first);
        Assert.Equal(QueueState.Paused, pc.Find(stuckId)!.State);
        Assert.Equal(QueueState.Failed, pc.Find(goneId)!.State);

        await pc.DetachAsync();
        await first.DisposeAsync(); // the server rebooting
        var second = StartAgent();
        var again = Pc(); // the app opened the next day
        await AttachAsync(again, second);
        Assert.Equal(QueueState.Paused, again.Find(stuckId)!.State);
        Assert.Equal(QueueState.Failed, again.Find(goneId)!.State);
        await again.WhenIdle.WaitAsync(Patience);

        stuck.HoldAt = null;
        stuck.Release();
        await again.ResumeAsync(stuckId).WaitAsync(Patience);
        await QueueWait.UntilAsync(again, () => again.Find(stuckId)?.State == QueueState.Done);
        Assert.Equal(stuck.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "stuck.mp4")));
    }

    [Fact]
    public async Task APcLeftOpenFollowsAnAgentThatRestarted()
    {
        // the first agent says a lot about the file (progress four times a second), the second, restarted, much less
        var a = AddFile("a", 4 * MiB, holdAt: MiB);
        var first = AgentHost.Start(Agent);
        var pc = Pc();
        await AttachAsync(pc, first);
        var id = await AddOneAsync(pc, "a");
        await a.Held.Task.WaitAsync(Patience);
        await QueueWait.UntilAsync(pc, () => _events.Count(e => e.Item.Id == id) > 20);
        await pc.DetachAsync();

        await first.DisposeAsync(); // the agent stopped (a reboot, an update) while the app stays open
        var second = StartAgent();
        await QueueWait.UntilAsync(second.Queue, () => second.Queue.Find(id)?.State == QueueState.Downloading);
        await AttachAsync(pc, second);

        Assert.Equal(QueueState.Downloading, pc.Find(id)!.State);
        await pc.PauseAsync(id).WaitAsync(Patience);
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.Paused);
        a.HoldAt = null;
        a.Release();
        await pc.ResumeAsync(id).WaitAsync(Patience);
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.Done);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
    }

    [Fact]
    public async Task AConnectionGoneQuietIsDroppedSoItCanBeReplaced()
    {
        var a = AddFile("a", holdAt: MiB);
        var host = StartAgent();
        var pc = Pc(heartbeat: TimeSpan.FromMilliseconds(300));
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var route = await AttachThroughRouteAsync(pc, host);
        pc.ConnectionChanged += attached =>
        {
            if (!attached)
                dropped.TrySetResult();
        };
        var id = await AddOneAsync(pc, "a");
        await a.Held.Task.WaitAsync(Patience);
        await Task.Delay(TimeSpan.FromSeconds(1)); // heartbeats answered meanwhile: still attached

        Assert.True(pc.IsAttached);
        route.Quiet = true;
        await dropped.Task.WaitAsync(Patience);

        Assert.False(pc.IsAttached);
        a.HoldAt = null;
        a.Release();
        await AttachAsync(pc, host); // what the session does next
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.Done);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
    }

    [Fact]
    public async Task RestartedAgentCarriesOnByItself()
    {
        var a = AddFile("a", 4 * MiB, holdAt: 3 * MiB);
        var first = AgentHost.Start(Agent);
        var pc = Pc();
        await AttachAsync(pc, first);
        var id = await AddOneAsync(pc, "a");
        await a.Held.Task.WaitAsync(Patience);
        await pc.DetachAsync();

        await first.DisposeAsync(); // the server rebooting
        a.HoldAt = null;
        a.Release();
        var second = StartAgent();
        await QueueWait.UntilAsync(second.Queue, () => second.Queue.Find(id)?.State == QueueState.Done);

        await AttachAsync(pc, second);
        await pc.WhenIdle.WaitAsync(Patience);
        Assert.Equal(QueueState.Done, pc.Find(id)!.State);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
    }

    [Fact]
    public async Task SettingsTravelToTheServer()
    {
        AddFile("a");
        await File.WriteAllTextAsync(Path.Combine(Directory.CreateDirectory(PcFolder).FullName, "a.mp4"), "mine");
        var host = StartAgent();
        var pc = Pc(Settings(("FERRY_FILES_AT_ONCE", "1"), ("FERRY_ON_EXISTING", "skip")));

        await AttachAsync(pc, host);
        var id = await AddOneAsync(pc, "a");
        await pc.WhenIdle.WaitAsync(Patience);

        Assert.Equal(1, host.Queue.FilesAtOnce);
        Assert.Contains("FERRY_FILES_AT_ONCE", await File.ReadAllTextAsync(Agent.SettingsFile), StringComparison.Ordinal);
        // "skip" applies on this PC; on the server the file is downloaded anyway, then dropped
        Assert.Equal(QueueState.Skipped, pc.Find(id)!.State);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(PcFolder, "a.mp4")));
        await QueueWait.UntilAsync(host.Queue, () => host.Queue.Items.Count == 0);
        Assert.Empty(ServerFiles);
    }

    [Fact]
    public async Task TheServerReadsLinksAndListsForThePc()
    {
        AddFile("a");
        _site.Static["/plain.bin"] = ("application/octet-stream", new byte[3]);
        _site.Static["/files/"] = ("text/html", """<title>Index of /files/</title><a href="a.bin">a.bin</a><a href="b.bin">b.bin</a>"""u8.ToArray());
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);
        var plain = new Uri(_site.Url, "plain.bin");

        var read = await pc.ReadAsync(TestSites.Link(Link("a")), RequestDetails.None, CancellationToken.None);
        var direct = await pc.ReadAsync(new FileLink(DirectSite.SiteId, plain, LinkKeys.WithoutFragment(plain)), RequestDetails.None, CancellationToken.None);
        var listed = await pc.ExpandAsync(new Uri(_site.Url, "files/"), RequestDetails.None, CancellationToken.None);
        var single = await pc.ExpandAsync(new Uri(Link("a")), RequestDetails.None, CancellationToken.None);
        var gone = await Assert.ThrowsAsync<StageException>(() => pc.ReadAsync(TestSites.Link(Link("gone")), RequestDetails.None, CancellationToken.None));

        Assert.Equal("a.mp4", read.Name);
        Assert.Equal(("plain.bin", (long?)3), direct);
        Assert.Equal("files", listed!.Title);
        Assert.Equal([new Uri(_site.Url, "files/a.bin").ToString(), new Uri(_site.Url, "files/b.bin").ToString()], listed.Links);
        Assert.Null(single);
        Assert.Equal(Stage.Resolve, gone.Stage);
        // the PC's own expander in server mode: what must read here first, then the server
        using var http = new HttpFetcher();
        var expanded = await new LinkExpander([.. OfficialSites.Listers(http, Settings(), secrets: null, downloadsElsewhere: true), new RemoteLister(pc)])
            .ExpandAsync([Link("a"), new Uri(_site.Url, "files/").ToString()], RequestDetails.None, CancellationToken.None);
        Assert.Equal([null, "files"], expanded.Lists.Select(list => list.Title));
        await pc.DetachAsync();
        Assert.Equal(Stage.Server, (await Assert.ThrowsAsync<StageException>(() => pc.ExpandAsync(new Uri(_site.Url, "files/"), RequestDetails.None, CancellationToken.None))).Stage);
    }

    [Fact]
    public void InServerModeThePcListsOnlyWhatItMust()
    {
        using var http = new HttpFetcher();
        Assert.NotEmpty(OfficialSites.Listers(http, Settings(), secrets: null, downloadsElsewhere: false));
        // Debrid would read here, with its key; without one nothing is left to read on this PC
        Assert.Empty(OfficialSites.Listers(http, Settings(), secrets: null, downloadsElsewhere: true));
        Assert.All(OfficialSites.Plugins.Where(plugin => plugin.ReadsListsHere), plugin => Assert.Equal("Debrid", plugin.Name));
    }

    [Fact]
    public async Task SettingsChangedWhileConnectedReachTheServer()
    {
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);
        Assert.Equal(2, host.Queue.FilesAtOnce);

        await pc.ReconfigureAsync(Settings(("FERRY_FILES_AT_ONCE", "1")));

        Assert.Equal(1, host.Queue.FilesAtOnce);
        Assert.Contains("FERRY_FILES_AT_ONCE", await File.ReadAllTextAsync(Agent.SettingsFile), StringComparison.Ordinal);
        await pc.DetachAsync();
        await pc.ReconfigureAsync(Settings(("FERRY_FILES_AT_ONCE", "4"))); // not connected: the next hello carries them
        Assert.Equal(1, host.Queue.FilesAtOnce);
        await AttachAsync(pc, host);
        Assert.Equal(4, host.Queue.FilesAtOnce);
    }

    [Fact]
    public async Task DetailsAreGivenBackToAnAgentThatRestarted()
    {
        var a = AddFile("a", 4 * MiB, holdAt: MiB);
        _site.RequiredCookie = "session=secret";
        var first = AgentHost.Start(Agent);
        var pc = Pc();
        await AttachAsync(pc, first);
        var id = Assert.Single((await pc.AddAsync([Link("a")], new AddOptions(Details: new RequestDetails(Cookies: "session=secret")))).Added).Id;
        await a.Held.Task.WaitAsync(Patience);
        await pc.DetachAsync();
        await first.DisposeAsync(); // the server rebooting: the agent keeps the login in memory only
        a.HoldAt = null;
        a.Release();

        var second = StartAgent();
        await QueueWait.UntilAsync(second.Queue, () => second.Queue.Find(id)?.State == QueueState.Failed); // it carried on without the login
        await AttachAsync(pc, second); // the PC gives it back

        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.Done);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
        Assert.Empty(pc.RequestsToKeep()); // done: the login is let go
    }

    [Fact]
    public async Task NewsRightAfterTheHelloIsNotUndoneByItsSnapshot()
    {
        var (pcEnd, agentEnd) = await ConnectedPairAsync();
        var id = Guid.NewGuid();
        var item = new RemoteItem(id, FakeFileSite.SiteId, Link("a"), "a", ServerFolder, null, QueueState.Waiting, null, 0, null, 0, 0, 0, 0, null, null, Revision: 1);
        var agent = Task.Run(async () =>
        {
            // an agent whose queue moves on right after it answers the hello: the snapshot says waiting, the event says downloading
            using var reader = new StreamReader(agentEnd, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(agentEnd, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            var hello = JsonSerializer.Deserialize(await reader.ReadLineAsync() ?? "", AgentJson.Default.AgentRequest)!;
            var answer = JsonSerializer.Serialize(new AgentMessage(Answers: hello.Id, Version: Product.Build, Items: [item]), AgentJson.Default.AgentMessage);
            var news = JsonSerializer.Serialize(new AgentMessage(Event: AgentEventKind.StateChanged, Item: item with { State = QueueState.Downloading, Revision = 2 },
                                                                 Was: QueueState.Waiting), AgentJson.Default.AgentMessage);
            await writer.WriteAsync(answer + "\n" + news + "\n");
        });
        var pc = Pc();

        await pc.AttachAsync(pcEnd, pcEnd);
        await agent;

        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.Downloading);
        Assert.Equal(QueueState.Downloading, Assert.Single(pc.Items).State);
    }

    [Fact]
    public async Task AnItemRemovedRightBeforeTheHelloIsAnsweredStaysGone()
    {
        var (pcEnd, agentEnd) = await ConnectedPairAsync();
        var other = Guid.NewGuid();
        var item = new RemoteItem(Guid.NewGuid(), FakeFileSite.SiteId, Link("a"), "a", ServerFolder, null, QueueState.Waiting, null, 0, null, 0, 0, 0, 0, null, null, Revision: 3);
        var agent = Task.Run(async () =>
        {
            // an agent whose snapshot was taken before the removal it tells of first: events and answers cross on the way out
            using var reader = new StreamReader(agentEnd, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(agentEnd, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            var hello = JsonSerializer.Deserialize(await reader.ReadLineAsync() ?? "", AgentJson.Default.AgentRequest)!;
            var removed = JsonSerializer.Serialize(new AgentMessage(Event: AgentEventKind.Removed, Item: item with { Revision = 4 }), AgentJson.Default.AgentMessage);
            var answer = JsonSerializer.Serialize(new AgentMessage(Answers: hello.Id, Version: Product.Build, Items: [item]), AgentJson.Default.AgentMessage);
            var stale = JsonSerializer.Serialize(new AgentMessage(Event: AgentEventKind.Updated, Item: item), AgentJson.Default.AgentMessage);
            var last = JsonSerializer.Serialize(new AgentMessage(Event: AgentEventKind.Added, Item: item with { Id = other, Link = Link("b"), Key = "b" }), AgentJson.Default.AgentMessage);
            await writer.WriteAsync(removed + "\n" + answer + "\n" + stale + "\n" + last + "\n");
        });
        var pc = Pc();

        await pc.AttachAsync(pcEnd, pcEnd);
        await agent;

        await QueueWait.UntilAsync(pc, () => pc.Find(other) is not null);
        Assert.Equal(other, Assert.Single(pc.Items).Id);
    }

    [Fact]
    public async Task AManyLinkAddIsAnsweredAndAHeartbeatDuringItToo()
    {
        const int Links = 5000;
        const int Add = 2, Heartbeat = 3;
        var host = StartAgent();
        var (pcEnd, agentEnd) = await ConnectedPairAsync();
        _ = host.ServeAsync(agentEnd, agentEnd, _stop.Token);
        using var reader = new StreamReader(pcEnd, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pcEnd, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        static string Line(AgentRequest request) => JsonSerializer.Serialize(request, AgentJson.Default.AgentRequest);
        await writer.WriteLineAsync(Line(new AgentRequest(1, AgentCommand.Hello, Version: Product.Build, Settings: Settings().Changed())));
        await reader.ReadLineAsync().WaitAsync(Patience);

        // the add is held at its first event until the heartbeat has been answered, which it can only be if the read loop
        // went on to the next line while the add was still being done: with nothing holding it, a quick add on a busy
        // machine could be answered first all the same, and say nothing about the read loop
        using var heartbeatAnswered = new ManualResetEventSlim();
        host.Queue.Changed += e =>
        {
            if (e is QueueItemAdded)
                heartbeatAnswered.Wait(Patience);
        };
        // sent together: the agent's read loop has the heartbeat as soon as it isn't the one adding
        await writer.WriteAsync(Line(new AgentRequest(Add, AgentCommand.Add, Lines: [.. Enumerable.Range(0, Links).Select(i => Link($"m{i}"))], Flag: true))
                                + "\n" + Line(new AgentRequest(Heartbeat, AgentCommand.Ping)) + "\n");
        var answered = new List<int>();
        while (answered.Count < 2)
        {
            var message = JsonSerializer.Deserialize(await reader.ReadLineAsync().WaitAsync(Patience) ?? "", AgentJson.Default.AgentMessage)!;
            if (message.Answers is not { } id)
                continue;
            answered.Add(id);
            if (id == Heartbeat)
                heartbeatAnswered.Set();
        }

        Assert.Equal([Heartbeat, Add], answered);
        Assert.Equal(Links, host.Queue.Items.Count);
        var store = new QueueStore(Agent.QueueFile);
        for (var tries = 0; store.Read().Count < Links && tries < 200; tries++)
            await Task.Delay(25);
        Assert.Equal(Links, store.Read().Count); // saved soon after, without a save for each
    }

    [Fact]
    public async Task SortingHappensWhereTheFilesArriveAndNotOnTheServer()
    {
        var a = AddFile("a", seed: 1);
        AddFile("b", seed: 2);
        var host = StartAgent();
        var pc = Pc(Settings(("FERRY_SORT_BY_TYPE", "true"), ("FERRY_SERVER_KEEP_FILES", "true")));
        await AttachAsync(pc, host);
        await pc.SetHeldAsync(true);

        var id = await AddOneAsync(pc, "a");
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.OnServer);

        // once the PC has heard it is done, `agent read` has to find it so
        Assert.Equal(Path.Combine(ServerFolder, "a.mp4"), AgentRead.FinishedFile(Agent, id));
        await pc.SetHeldAsync(false);
        var album = Assert.Single((await pc.AddAsync([Link("b")], new AddOptions(Album: "Summer"))).Added);
        await pc.WhenIdle.WaitAsync(Patience);

        Assert.Equal(Path.Combine(PcFolder, "Videos", "a.mp4"), pc.Find(id)!.Path);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(pc.Find(id)!.Path!));
        Assert.Equal(Path.Combine(PcFolder, "Summer", "b.mp4"), pc.Find(album.Id)!.Path);
    }

    [Fact]
    public async Task TheRulesForFilesAreNotForOneTheUserNamedOrAnAlbums()
    {
        AddFile("a", seed: 1);
        AddFile("b", seed: 2);
        AddFile("c", seed: 3);
        var host = StartAgent();
        var pc = Pc(Settings(("FERRY_PLUGIN_AFTER_DOWNLOAD", "true"), ("FERRY_RENAME_RULES", "*.mp4=ruled {name}{ext}")));
        await AttachAsync(pc, host);

        var plain = await AddOneAsync(pc, "a");
        var named = Assert.Single((await pc.AddAsync([Link("b")], new AddOptions(Name: "mine.mp4"))).Added);
        var album = Assert.Single((await pc.AddAsync([Link("c")], new AddOptions(Album: "Summer"))).Added);
        await pc.WhenIdle.WaitAsync(Patience);

        Assert.Equal(Path.Combine(PcFolder, "ruled a.mp4"), pc.Find(plain)!.Path);
        Assert.Equal(Path.Combine(PcFolder, "mine.mp4"), pc.Find(named.Id)!.Path);
        Assert.Equal(Path.Combine(PcFolder, "Summer", "c.mp4"), pc.Find(album.Id)!.Path);
    }

    [Fact]
    public async Task APcIsNotDoneUntilTheServerHasHeardAFileCameOver()
    {
        var data = new byte[1000];
        var path = Path.Combine(Directory.CreateDirectory(ServerFolder).FullName, "a.mp4");
        await File.WriteAllBytesAsync(path, data);
        var item = new RemoteItem(Guid.NewGuid(), FakeFileSite.SiteId, Link("a"), "a", ServerFolder, null, QueueState.Done, path, data.Length, data.Length, 0, 0, 0, 0, null, null, Revision: 1);
        var (pcEnd, agentEnd) = await ConnectedPairAsync();
        var (told, answer) = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _ = Task.Run(async () =>
        {
            // an agent that takes its time over hearing the file was brought over
            using var reader = new StreamReader(agentEnd, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(agentEnd, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            while (await reader.ReadLineAsync() is { } line)
            {
                var request = JsonSerializer.Deserialize(line, AgentJson.Default.AgentRequest)!;
                if (request.Command == AgentCommand.Collected)
                {
                    told.TrySetResult();
                    await answer.Task;
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(request.Command switch
                {
                    AgentCommand.Hello => new AgentMessage(Answers: request.Id, Version: Product.Build, Items: [item]),
                    AgentCommand.FileInfo => new AgentMessage(Answers: request.Id, Path: path, Size: data.Length, Sha256: Convert.ToHexStringLower(SHA256.HashData(data))),
                    _ => new AgentMessage(Answers: request.Id),
                }, AgentJson.Default.AgentMessage));
            }
        });
        var pc = Pc();

        await pc.AttachAsync(pcEnd, pcEnd);
        await told.Task.WaitAsync(Patience);

        Assert.Equal(QueueState.Done, Assert.Single(pc.Items).State);
        Assert.False(pc.WhenCollected.IsCompleted);
        Assert.False(pc.WhenIdle.IsCompleted);
        answer.SetResult();
        await pc.WhenCollected.WaitAsync(Patience);
        await pc.WhenIdle.WaitAsync(Patience);
    }

    [Fact]
    public async Task AQueueHeldFromTheStartBringsNothingOverWhenItAttaches()
    {
        AddFile("a");
        var host = StartAgent();
        var first = Pc();
        await AttachAsync(first, host);
        var id = await AddOneAsync(first, "a");
        await first.DetachAsync();
        await QueueWait.UntilAsync(host.Queue, () => host.Queue.Find(id)?.State == QueueState.Done);

        var looking = Pc();
        await looking.SetHeldAsync(true);
        await AttachAsync(looking, host);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal(QueueState.OnServer, looking.Find(id)!.State);
        Assert.Empty(_files.Opened);
        Assert.Equal(QueueState.Done, host.Queue.Find(id)!.State);
    }

    [Fact]
    public async Task AFinishedFileChangedOnTheServerIsHashedAgain()
    {
        var a = AddFile("a");
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);
        await pc.SetHeldAsync(true);
        var id = await AddOneAsync(pc, "a");
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.OnServer);
        var ask = new AgentRequest(0, AgentCommand.FileInfo, Item: id);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(a.Data)), (await pc.RequestAsync(ask)).Sha256);

        var changed = new byte[1000];
        await File.WriteAllBytesAsync(Path.Combine(ServerFolder, "a.mp4"), changed);
        var now = await pc.RequestAsync(ask);

        Assert.Equal(changed.Length, now.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(changed)), now.Sha256);
    }

    [Fact]
    public async Task HeldTransfersWaitWhileTheServerKeepsDownloading()
    {
        var a = AddFile("a");
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);

        await pc.SetHeldAsync(true);
        var id = await AddOneAsync(pc, "a");
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.OnServer);
        await QueueWait.UntilAsync(host.Queue, () => host.Queue.Find(id)?.State == QueueState.Done);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal(QueueState.OnServer, pc.Find(id)!.State);
        Assert.DoesNotContain(QueueState.Transferring, StatesOf(id));
        await pc.SetHeldAsync(false);
        await QueueWait.UntilAsync(pc, () => pc.Find(id)?.State == QueueState.Done);
        Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
    }

    [Fact]
    public async Task DuplicatesAndOtherLinksComeBackFromTheServer()
    {
        AddFile("a");
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);

        var result = await pc.AddAsync([Link("a"), Link("a", "mirror.test"), "not a link"]);

        var item = Assert.Single(result.Added);
        Assert.Equal(item.Id, Assert.Single(result.Duplicates).Id);
        Assert.Equal("not a link", Assert.Single(result.Rejected).Text);
        await pc.WhenIdle.WaitAsync(Patience);
    }

    [Fact]
    public async Task RemovingAFileStillOnTheServerRemovesItThere()
    {
        var a = AddFile("a", holdAt: MiB);
        var host = StartAgent();
        var pc = Pc();
        await AttachAsync(pc, host);
        var id = await AddOneAsync(pc, "a");
        await a.Held.Task.WaitAsync(Patience);

        await pc.RemoveAsync(id, deleteParts: true);

        Assert.Empty(pc.Items);
        Assert.Empty(host.Queue.Items);
        Assert.Empty(ServerFiles);
        Assert.Equal(id, _events.OfType<QueueItemRemoved>().Single().Item.Id);
        a.Release();
    }

    [Fact]
    public async Task AgentOfAnotherVersionIsRefused()
    {
        var host = StartAgent(version: "0.0.0");
        var pc = Pc();

        var refused = await Assert.ThrowsAsync<AgentVersionException>(() => AttachAsync(pc, host));

        Assert.Equal("0.0.0", refused.AgentVersion);
        Assert.False(pc.IsAttached);
    }

    [Fact]
    public async Task NothingIsAnsweredBeforeHello()
    {
        var host = StartAgent();
        var (pcEnd, agentEnd) = await ConnectedPairAsync();
        _ = host.ServeAsync(agentEnd, agentEnd, _stop.Token);
        using var reader = new StreamReader(pcEnd, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pcEnd, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n", AutoFlush = true };

        await writer.WriteLineAsync("""{"id":7,"command":"PauseAll"}""");
        var refused = await reader.ReadLineAsync().WaitAsync(Patience);
        await writer.WriteLineAsync("rm -rf /");
        var garbage = await reader.ReadLineAsync().WaitAsync(Patience);

        Assert.Contains("\"answers\":7", refused, StringComparison.Ordinal);
        Assert.Contains("Say hello first", refused, StringComparison.Ordinal);
        Assert.Contains("\"answers\":0", garbage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentListensOnAUnixSocket()
    {
        var a = AddFile("a");
        var shortRoot = Path.Combine(Path.GetTempPath(), "bk" + Guid.NewGuid().ToString("N")[..6]);
        var host = AgentHost.Start(new AgentPaths(shortRoot));
        _running.Add(host);
        try
        {
            var listening = host.ListenAsync(_stop.Token);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _sockets.Add(socket);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(new AgentPaths(shortRoot).SocketPath));
            var stream = new NetworkStream(socket, ownsSocket: false);
            var pc = Pc();

            await pc.AttachAsync(stream, stream);
            await AddOneAsync(pc, "a");
            await pc.WhenIdle.WaitAsync(Patience);

            Assert.Equal(a.Data, await File.ReadAllBytesAsync(Path.Combine(PcFolder, "a.mp4")));
            Assert.False(listening.IsCompleted);
        }
        finally
        {
            await _stop.CancelAsync();
            await host.DisposeAsync();
            _running.Remove(host);
            Directory.Delete(shortRoot, recursive: true);
        }
    }
}
