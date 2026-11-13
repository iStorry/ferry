using System.Collections.Concurrent;
using System.Globalization;
using Ferry.Core;
using Ferry.Core.Download;
using Ferry.Core.Http;
using Ferry.Core.Queue;

namespace Ferry.Core.Tests;

/// <summary>The queue against <see cref="FakeSite"/>: files at once, the per-host cap, stopping, retrying, restoring.</summary>
public sealed class QueueTests : IAsyncLifetime, IDisposable
{
    private const int MiB = 1 << 20;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private readonly FakeSite _site = new();
    private readonly HttpFetcher _http = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("ferry_queue_").FullName;
    private readonly ConcurrentQueue<QueueEvent> _events = new();
    private readonly List<DownloadQueue> _queues = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var queue in _queues)
            await queue.DisposeAsync().AsTask().WaitAsync(Patience);
    }

    public void Dispose()
    {
        _http.Dispose();
        _site.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private DownloadQueue Queue(int filesAtOnce = 2, int connections = 2, int? perHost = null,
                                ExistingFile onExisting = ExistingFile.Rename)
    {
        var site = _site.Options();
        var options = DownloadOptions.For(DownloadProfile.Balanced, connections, onExisting) with
        {
            StallTimeout = TimeSpan.FromSeconds(5),
            Retry = RetryPolicy.Untimed(maxRetries: 3, wait: TimeSpan.FromMilliseconds(1)),
        };
        var hosts = perHost is { } cap ? new HostConnections(cap) : null;
        var downloader = new SegmentedDownload(_http, TestSites.Only(_http, site), options, hosts: hosts);
        var queue = new DownloadQueue(downloader, TestSites.Only(_http, site), _folder, filesAtOnce);
        queue.Changed += _events.Enqueue;
        _queues.Add(queue);
        return queue;
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

    private string Saved(string slug) => Path.Combine(_folder, $"{slug}.mp4");

    private string[] FolderFiles => [.. Directory.GetFiles(_folder).Select(Path.GetFileName).OfType<string>().Order()];

    private static async Task<Guid> AddOneAsync(DownloadQueue queue, string slug) => Assert.Single((await queue.AddAsync([Link(slug)])).Added).Id;

    /// <summary>The item once <paramref name="condition"/> holds for it.</summary>
    private static async Task<QueueItem> UntilAsync(DownloadQueue queue, Guid id, Func<QueueItem, bool> condition)
    {
        var reached = new TaskCompletionSource<QueueItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(QueueEvent e)
        {
            if (e.Item.Id == id && condition(e.Item))
                reached.TrySetResult(e.Item);
        }
        queue.Changed += Watch;
        try
        {
            if (queue.Find(id) is { } now && condition(now))
                return now;
            return await reached.Task.WaitAsync(Patience);
        }
        finally
        {
            queue.Changed -= Watch;
        }
    }

    private static Task<QueueItem> UntilStateAsync(DownloadQueue queue, Guid id, QueueState state) =>
        UntilAsync(queue, id, i => i.State == state);

    [Fact]
    public async Task FilesRunAtMostFilesAtOnceInTheOrderAdded()
    {
        var files = new[] { AddFile("a", seed: 1, holdAt: MiB), AddFile("b", seed: 2, holdAt: MiB), AddFile("c", seed: 3) };
        var queue = Queue(filesAtOnce: 2, connections: 1);

        var added = (await queue.AddAsync([Link("a"), Link("b"), Link("c")])).Added;
        await Task.WhenAll(files[0].Held.Task, files[1].Held.Task).WaitAsync(Patience);

        Assert.Equal([QueueState.Downloading, QueueState.Downloading, QueueState.Waiting], queue.Items.Select(i => i.State));
        Assert.False(queue.WhenIdle.IsCompleted);
        foreach (var file in files)
            file.Release();
        await queue.WhenIdle.WaitAsync(Patience);

        Assert.All(queue.Items, i => Assert.Equal(QueueState.Done, i.State));
        Assert.Equal(added.Select(i => i.Id), queue.Items.Select(i => i.Id));
        Assert.Equal(files[0].Data, await File.ReadAllBytesAsync(Saved("a")));
        Assert.Equal(files[1].Data, await File.ReadAllBytesAsync(Saved("b")));
        Assert.Equal(files[2].Data, await File.ReadAllBytesAsync(Saved("c")));
        var done = queue.Items[2];
        Assert.Equal((Saved("c"), 2L * MiB, (long?)(2 * MiB)), (done.Path, done.Done, done.Total));
    }

    [Fact]
    public async Task RaisingFilesAtOnceStartsWaitingFiles()
    {
        var (a, b) = (AddFile("a", seed: 1, holdAt: MiB), AddFile("b", seed: 2, holdAt: MiB));
        var queue = Queue(filesAtOnce: 1, connections: 1);
        await queue.AddAsync([Link("a"), Link("b")]);
        await a.Held.Task.WaitAsync(Patience);
        Assert.Equal(QueueState.Waiting, queue.Items[1].State);

        queue.FilesAtOnce = 2;

        await b.Held.Task.WaitAsync(Patience);
        a.Release();
        b.Release();
        await queue.WhenIdle.WaitAsync(Patience);
        Assert.All(queue.Items, i => Assert.Equal(QueueState.Done, i.State));
    }

    [Fact]
    public async Task ConnectionsPerHostAreCappedAcrossFiles()
    {
        var files = new[] { AddFile("a", 4 * MiB, seed: 1), AddFile("b", 4 * MiB, seed: 2) };
        foreach (var file in files)
            file.ChunkDelay = TimeSpan.FromMilliseconds(5);
        var queue = Queue(filesAtOnce: 2, connections: 4, perHost: 3);

        await queue.AddAsync([Link("a"), Link("b")]);
        await queue.WhenIdle.WaitAsync(Patience);

        Assert.All(queue.Items, i => Assert.Equal(QueueState.Done, i.State));
        Assert.Equal(files[0].Data, await File.ReadAllBytesAsync(Saved("a")));
        Assert.Equal(files[1].Data, await File.ReadAllBytesAsync(Saved("b")));
        Assert.InRange(_site.CdnMostActive, 2, 3);
    }

    [Fact]
    public async Task PausedFileContinuesWhereItStopped()
    {
        var file = AddFile("a", 4 * MiB, holdAt: 3 * MiB);
        var queue = Queue();
        var id = await AddOneAsync(queue, "a");
        await file.Held.Task.WaitAsync(Patience);
        // every part before the hold is down: a part that hadn't started yet would rightly start at byte 0 after resuming
        await UntilAsync(queue, id, i => i.Done >= 3 * MiB);

        await queue.PauseAsync(id);

        var paused = queue.Find(id)!;
        Assert.Equal(QueueState.Paused, paused.State);
        Assert.Equal(["a.mp4.ferry-part", "a.mp4.ferry-part.json"], FolderFiles);
        Assert.True(queue.WhenIdle.IsCompleted);
        var rangesBefore = file.Ranges.Count;
        file.HoldAt = null;
        file.Release();

        await queue.ResumeAsync(id);
        var done = await UntilStateAsync(queue, id, QueueState.Done);

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved("a")));
        Assert.Equal(["a.mp4"], FolderFiles);
        Assert.Equal(4L * MiB, done.Done);
        Assert.DoesNotContain(file.Ranges.Skip(rangesBefore), r => r is not null && r.StartsWith("bytes=0-", StringComparison.Ordinal) && r != "bytes=0-0");
        var states = _events.OfType<QueueItemStateChanged>().Select(e => e.Item.State).ToList();
        Assert.Equal([QueueState.Resolving, QueueState.Downloading, QueueState.Paused, QueueState.Waiting, QueueState.Resolving,
                      QueueState.Downloading, QueueState.Done], states);
    }

    [Fact]
    public async Task CancelledFileStaysStoppedUntilRetried()
    {
        var file = AddFile("a", holdAt: MiB);
        var queue = Queue();
        var id = await AddOneAsync(queue, "a");
        await file.Held.Task.WaitAsync(Patience);

        await queue.CancelAsync(id);
        await queue.ResumeAllAsync();

        Assert.Equal(QueueState.Cancelled, queue.Find(id)!.State);
        Assert.Contains("a.mp4.ferry-part", FolderFiles);
        file.HoldAt = null;
        file.Release();
        await queue.RetryAsync(id);
        await UntilStateAsync(queue, id, QueueState.Done);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved("a")));
    }

    [Fact]
    public async Task FailedFileSaysWhichStepFailedAndCanBeRetried()
    {
        var queue = Queue();
        var id = await AddOneAsync(queue, "gone");

        var failed = await UntilStateAsync(queue, id, QueueState.Failed);

        Assert.Equal(Stage.Resolve, failed.FailedAt);
        Assert.Contains("404", failed.Error!, StringComparison.Ordinal);
        var file = AddFile("gone");
        await queue.RetryAsync(id);
        var done = await UntilStateAsync(queue, id, QueueState.Done);
        Assert.Null(done.Error);
        Assert.Null(done.FailedAt);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved("gone")));
    }

    [Fact]
    public async Task DuplicatesAndOtherLinksAreNotAdded()
    {
        AddFile("a");
        var queue = Queue();

        var result = await queue.AddAsync([Link("a"), "", "# my links", Link("a", "mirror.test"), "not a link", "https://example.com/f/b"]);

        var item = Assert.Single(result.Added);
        Assert.Equal(item.Id, Assert.Single(result.Duplicates).Id);
        Assert.Equal(["https://example.com/f/b", "not a link"], result.Rejected.Select(r => r.Text).Order());
        Assert.Equal(item.Id, Assert.Single((await queue.AddAsync([Link("a")])).Duplicates).Id);
        await queue.WhenIdle.WaitAsync(Patience);
        Assert.Single(queue.Items);
    }

    [Fact]
    public async Task RemovingStopsTheFileAndDeletesItsPartsWhenAsked()
    {
        var (a, b) = (AddFile("a", seed: 1, holdAt: MiB), AddFile("b", seed: 2, holdAt: MiB));
        var queue = Queue();
        var (idA, idB) = (await AddOneAsync(queue, "a"), await AddOneAsync(queue, "b"));
        await Task.WhenAll(a.Held.Task, b.Held.Task).WaitAsync(Patience);

        await queue.RemoveAsync(idA, deleteParts: true);
        await queue.RemoveAsync(idB, deleteParts: false);

        Assert.Empty(queue.Items);
        Assert.Equal(["b.mp4.ferry-part", "b.mp4.ferry-part.json"], FolderFiles);
        Assert.Equal([idA, idB], _events.OfType<QueueItemRemoved>().Select(e => e.Item.Id));
        a.Release();
        b.Release();
    }

    [Fact]
    public async Task ExistingFileIsSkippedWhenAsked()
    {
        AddFile("a");
        await File.WriteAllTextAsync(Saved("a"), "mine");
        var queue = Queue(onExisting: ExistingFile.Skip);

        var skipped = await UntilStateAsync(queue, await AddOneAsync(queue, "a"), QueueState.Skipped);

        Assert.Equal(Saved("a"), skipped.Path);
        Assert.Equal("mine", await File.ReadAllTextAsync(Saved("a")));
    }

    [Fact]
    public async Task QueueComesBackAfterARestartPaused()
    {
        var file = AddFile("a", 4 * MiB, holdAt: 3 * MiB);
        var store = new QueueStore(Path.Combine(_folder, "config", "queue.json"));
        var first = Queue();
        var id = await AddOneAsync(first, "a");
        await file.Held.Task.WaitAsync(Patience);
        await UntilAsync(first, id, i => i.Done >= 2 * MiB);
        var failed = new SavedQueueItem(Guid.NewGuid(), Link("gone"), _folder, "renamed.mp4", QueueState.Failed, null, 0, null,
                                        500 << 10, "gone", Stage.Resolve);
        store.Save(first.Items);
        await first.DisposeAsync(); // the app closing: the download stops, its part stays
        file.HoldAt = null;
        file.Release();

        var second = Queue();
        var restored = second.Restore([.. store.Load(), failed, SavedQueueItem.From(first.Items[0])], new Dictionary<Guid, RequestDetails>());

        Assert.Single(restored.Duplicates);
        Assert.Equal([(id, QueueState.Paused), (failed.Id, QueueState.Failed)], second.Items.Select(i => (i.Id, i.State)));
        var gone = second.Items[1];
        Assert.Equal(("renamed.mp4", 500L << 10, "gone", (Stage?)Stage.Resolve), (gone.Name, gone.SpeedLimit, gone.Error, gone.FailedAt));
        Assert.True(second.WhenIdle.IsCompleted); // nothing starts by itself
        var rangesBefore = file.Ranges.Count;

        await second.ResumeAsync(id);
        await UntilStateAsync(second, id, QueueState.Done);

        Assert.Equal(file.Data, await File.ReadAllBytesAsync(Saved("a")));
        Assert.DoesNotContain(file.Ranges.Skip(rangesBefore), r => r is not null && r.StartsWith("bytes=0-", StringComparison.Ordinal) && r != "bytes=0-0");
    }

    [Fact]
    public void UnreadableQueueFileIsMovedAside()
    {
        var path = Path.Combine(_folder, "queue.json");
        File.WriteAllText(path, "[{ broken");

        Assert.Empty(new QueueStore(path).Load());
        Assert.Equal(["queue.json.unreadable"], FolderFiles);
    }

    [Fact]
    public async Task RenameAndSpeedLimitApplyToTheFile()
    {
        var file = AddFile("a", holdAt: MiB);
        var queue = Queue(filesAtOnce: 1);
        var held = AddFile("b", seed: 2, holdAt: 0);
        var (idB, idA) = (await AddOneAsync(queue, "b"), await AddOneAsync(queue, "a"));
        await queue.RenameAsync(idA, "my clip.mp4");
        await queue.SetSpeedLimitAsync(idA, 10 * MiB);
        Assert.Equal(("my clip.mp4", 10L * MiB), (queue.Find(idA)!.Name, queue.Find(idA)!.SpeedLimit));

        await queue.RemoveAsync(idB, deleteParts: true);
        await file.Held.Task.WaitAsync(Patience);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RenameAsync(idA, "too late.mp4"));
        file.Release();
        var done = await UntilStateAsync(queue, idA, QueueState.Done);

        Assert.Equal(Path.Combine(_folder, "my clip.mp4"), done.Path);
        Assert.Equal("my clip.mp4", done.DisplayName);
        Assert.Equal(file.Data, await File.ReadAllBytesAsync(done.Path!));
        held.Release();
    }
}
