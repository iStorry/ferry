using Ferry.Core.Files;
using System.Diagnostics.CodeAnalysis;
using Ferry.Core.Sites;
using Ferry.Core.Download;
using Ferry.Core.Http;

namespace Ferry.Core.Queue;

/// <summary>
/// The files to download on this machine, <see cref="FilesAtOnce"/> at a time, in the order they were added. Each can
/// be paused, resumed, cancelled, retried or removed; stopping one keeps what it downloaded, so it continues later. A
/// link already in the queue is not added twice. Changes are raised through <see cref="Changed"/>.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "It is a queue of downloads, though not a Queue<T>.")]
public sealed class DownloadQueue : IDownloadQueue
{
    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];
    private readonly Action<string, Exception>? _log;
    private SegmentedDownload _downloader;
    private SiteCatalog _sites;
    private string _folder;
    private int _filesAtOnce;
    private TaskCompletionSource _idle = Completed();
    private bool _held;

    /// <param name="downloader">runs each file; share its <see cref="HostConnections"/> to cap connections per server</param>
    /// <param name="sites">says which links can be queued; the same the downloader asks</param>
    /// <param name="folder">where files go</param>
    /// <param name="log">where a listener's failure goes (<see cref="QueueListeners"/>)</param>
    public DownloadQueue(SegmentedDownload downloader, SiteCatalog sites, string folder, int filesAtOnce, Action<string, Exception>? log = null)
    {
        (_downloader, _sites, _folder, _filesAtOnce, _log) = (downloader, sites, folder, Math.Max(1, filesAtOnce), log);
    }

    public event Action<QueueEvent>? Changed;

    /// <summary>
    /// Hold the queue, or let it go: while held nothing starts, and what was downloading goes back in line, keeping what it
    /// downloaded, to continue when the queue is let go.
    /// </summary>
    public async Task SetHeldAsync(bool held)
    {
        List<Guid> stopping;
        lock (_lock)
        {
            _held = held;
            stopping = held ? [.. _entries.Where(e => e.IsRunning).Select(e => e.Id)] : [];
        }
        await Task.WhenAll(stopping.Select(id => StopAsync(id, QueueState.Waiting)));
        Pump();
    }

    /// <summary>Files downloaded at the same time; raising it starts waiting files at once.</summary>
    public int FilesAtOnce
    {
        get
        {
            lock (_lock)
                return _filesAtOnce;
        }
        set
        {
            lock (_lock)
                _filesAtOnce = Math.Max(1, value);
            Pump();
        }
    }

    public IReadOnlyList<QueueItem> Items
    {
        get
        {
            lock (_lock)
                return [.. _entries.Select(e => e.Snapshot())];
        }
    }

    public Task WhenIdle
    {
        get
        {
            lock (_lock)
                return _idle.Task;
        }
    }

    /// <summary>
    /// Use new settings: files that start from now on use <paramref name="downloader"/> and go to
    /// <paramref name="folder"/>; files already downloading carry on as they were.
    /// </summary>
    public void Reconfigure(SegmentedDownload downloader, SiteCatalog sites, string folder, int filesAtOnce)
    {
        lock (_lock)
            (_downloader, _sites, _folder, _filesAtOnce) = (downloader, sites, folder, Math.Max(1, filesAtOnce));
        Pump();
    }

    public QueueItem? Find(Guid id)
    {
        lock (_lock)
            return Get(id)?.Snapshot();
    }

    public Task<QueueAddResult> AddAsync(IEnumerable<string> lines, AddOptions? options = null)
    {
        var (added, duplicates, rejected) = (new List<QueueItem>(), new List<QueueItem>(), new List<RejectedLink>());
        lock (_lock)
        {
            foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
            {
                if (!_sites.TryParse(line, out var link, out var reason))
                    rejected.Add(new RejectedLink(line, reason));
                else if (Same(link) is { } existing)
                    duplicates.Add(existing.Snapshot());
                else
                    added.Add(Append(new Entry(Guid.CreateVersion7(), link, options?.Folder ?? (options?.Album is { } album ? Into(_folder, album) : _folder))
                    {
                        Name = options?.Name,
                        Details = options?.Details ?? RequestDetails.None,
                        FolderChosen = options is { Folder: not null } or { Album: not null },
                        Sha256 = options?.Sha256,
                        State = options is { Paused: true } ? QueueState.Paused : QueueState.Waiting,
                    }).Snapshot());
            }
        }
        Raise(added.Select(i => new QueueItemAdded(i)));
        Pump();
        return Task.FromResult(new QueueAddResult(added, duplicates, rejected));
    }

    /// <summary>A folder of its own under <paramref name="root"/>, named <paramref name="folder"/> made safe; the root if that leaves nothing.</summary>
    public static string Into(string root, string folder) =>
        FileNames.Sanitize(folder.Trim()) is { Length: > 0 } name && name.Trim('.').Length > 0 ? Path.Combine(root, name) : root;

    /// <summary>
    /// Put back items saved by <see cref="QueueStore"/>. A file that was waiting or downloading comes back paused, so
    /// nothing starts without being asked, unless <paramref name="resumeInterrupted"/> puts it back in line.
    /// </summary>
    /// <param name="requests">the request details saved with them (<see cref="RequestStore"/>), by item</param>
    /// <param name="resumeInterrupted">for the server's agent, where nobody is there to click Resume</param>
    public QueueAddResult Restore(IEnumerable<SavedQueueItem> saved, IReadOnlyDictionary<Guid, RequestDetails> requests, bool resumeInterrupted = false)
    {
        var (added, duplicates, rejected) = (new List<QueueItem>(), new List<QueueItem>(), new List<RejectedLink>());
        lock (_lock)
        {
            foreach (var item in saved)
            {
                if (!_sites.TryRead(item.Link, out var link, out var reason))
                {
                    rejected.Add(new RejectedLink(item.Link, reason));
                    continue;
                }
                if (Same(link) is { } existing)
                {
                    duplicates.Add(existing.Snapshot());
                    continue;
                }
                var state = item.State is QueueState.Waiting or QueueState.Resolving or QueueState.Downloading
                    ? resumeInterrupted ? QueueState.Waiting : QueueState.Paused
                    : item.State;
                var entry = new Entry(item.Id, link, item.Folder)
                {
                    Name = item.Name,
                    State = state,
                    Path = item.Path,
                    Done = item.Done,
                    Total = item.Total,
                    Error = item.Error,
                    FailedAt = item.FailedAt,
                    Details = requests.GetValueOrDefault(item.Id, RequestDetails.None),
                    FolderChosen = item.FolderChosen,
                    Sha256 = item.Sha256,
                };
                entry.Limit.BytesPerSecond = Math.Max(0, item.SpeedLimit);
                added.Add(Append(entry).Snapshot());
            }
        }
        Raise(added.Select(i => new QueueItemAdded(i)));
        Pump();
        return new QueueAddResult(added, duplicates, rejected);
    }

    /// <summary>The request details of files that may still need them, to save apart from the queue.</summary>
    public Dictionary<Guid, RequestDetails> RequestsToKeep()
    {
        lock (_lock)
            return _entries.Where(e => !e.Details.IsEmpty && e.State is not (QueueState.Done or QueueState.Skipped))
                           .ToDictionary(e => e.Id, e => e.Details);
    }

    public Task PauseAsync(Guid id) => StopAsync(id, QueueState.Paused, QueueState.Waiting);

    public Task CancelAsync(Guid id) => StopAsync(id, QueueState.Cancelled, QueueState.Waiting, QueueState.Paused);

    public Task ResumeAsync(Guid id) => Requeue(id, QueueState.Paused);

    public Task RetryAsync(Guid id) => Requeue(id, QueueState.Failed, QueueState.Cancelled);

    public async Task PauseAllAsync()
    {
        List<Guid> ids;
        lock (_lock)
            ids = [.. _entries.Select(e => e.Id)];
        await Task.WhenAll(ids.Select(PauseAsync));
    }

    public async Task ResumeAllAsync()
    {
        List<Guid> ids;
        lock (_lock)
            ids = [.. _entries.Where(e => e.State == QueueState.Paused).Select(e => e.Id)];
        foreach (var id in ids)
            await ResumeAsync(id);
    }

    public async Task RemoveAsync(Guid id, bool deleteParts)
    {
        lock (_lock)
        {
            if (Get(id) is { } removing)
                removing.Removing = true; // a retry while it stops must not start it again
        }
        await CancelAsync(id);
        QueueItem removed;
        lock (_lock)
        {
            if (Get(id) is not { } entry)
                return;
            _entries.Remove(entry);
            removed = entry.Next();
        }
        if (deleteParts && removed is { Path: { } path, State: not (QueueState.Done or QueueState.Skipped) })
            PartFile.TryDelete(path); // removed from the queue either way
        Raise([new QueueItemRemoved(removed)]);
        Pump();
    }

    public Task RenameAsync(Guid id, string? name)
    {
        QueueItem renamed;
        lock (_lock)
        {
            if (Get(id) is not { } entry)
                return Task.CompletedTask;
            if (entry.Path is not null || entry.IsRunning)
                return Task.FromException(new InvalidOperationException("The file has already started under its name."));
            entry.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            renamed = entry.Next();
        }
        Raise([new QueueItemUpdated(renamed)]);
        return Task.CompletedTask;
    }

    public Task SetSpeedLimitAsync(Guid id, long bytesPerSecond)
    {
        QueueItem changed;
        lock (_lock)
        {
            if (Get(id) is not { } entry)
                return Task.CompletedTask;
            entry.Limit.BytesPerSecond = Math.Max(0, bytesPerSecond);
            changed = entry.Next();
        }
        Raise([new QueueItemUpdated(changed)]);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Give a file the request details it was added with, again: a server's agent keeps them in memory only, so after
    /// it restarts the PC sends them once more. A file that still has its details keeps them. One without that failed
    /// meanwhile goes back in line with them, and one that is running without them starts over with them, from what it
    /// downloaded.
    /// </summary>
    public async Task SetDetailsAsync(Guid id, RequestDetails details)
    {
        bool restart;
        lock (_lock)
        {
            if (Get(id) is not { Details.IsEmpty: true } entry)
                return;
            entry.Details = details;
            restart = entry.IsRunning;
        }
        if (restart)
            await StopAsync(id, QueueState.Waiting);
        else
            await Requeue(id, QueueState.Failed);
    }

    /// <summary>Pause everything, so it continues next time.</summary>
    public async ValueTask DisposeAsync() => await PauseAllAsync();

    private Entry? Get(Guid id) => _entries.Find(e => e.Id == id);

    private Entry? Same(FileLink link) => _entries.Find(e => e.Link.SiteId == link.SiteId && e.Link.Key == link.Key);

    private Entry Append(Entry entry)
    {
        _entries.Add(entry);
        return entry;
    }

    /// <param name="from">states a file that isn't running can be stopped from</param>
    private async Task StopAsync(Guid id, QueueState to, params QueueState[] from)
    {
        Task? running = null;
        QueueEvent? changed = null;
        lock (_lock)
        {
            if (Get(id) is not { } entry)
                return;
            if (entry.IsRunning)
            {
                // holding the queue doesn't bring back a file already being cancelled or paused
                if (to != QueueState.Waiting || !entry.Stop!.IsCancellationRequested)
                    entry.StopAs = to;
                _ = entry.Stop!.CancelAsync(); // the download's cancellation callbacks run outside the lock
                running = entry.Run;
            }
            else if (from.Contains(entry.State))
            {
                changed = entry.MoveTo(to);
            }
        }
        if (changed is not null)
            Raise([changed]);
        if (running is not null)
            await running;
        Pump();
    }

    private Task Requeue(Guid id, params QueueState[] from)
    {
        QueueEvent? changed = null;
        lock (_lock)
        {
            if (Get(id) is { Removing: false } entry)
            {
                if (!entry.IsRunning && from.Contains(entry.State))
                {
                    (entry.Error, entry.FailedAt) = (null, null);
                    changed = entry.MoveTo(QueueState.Waiting);
                }
                else if (entry is { IsRunning: true, Stop.IsCancellationRequested: true } && from.Contains(entry.StopAs))
                {
                    entry.StopAs = QueueState.Waiting; // asked back while still stopping: it ends in line again
                }
            }
        }
        if (changed is not null)
            Raise([changed]);
        Pump();
        return Task.CompletedTask;
    }

    /// <summary>Start waiting files while there are free slots, and note whether the queue is idle.</summary>
    private void Pump()
    {
        var started = new List<QueueEvent>();
        lock (_lock)
        {
            var running = _entries.Count(e => e.IsRunning);
            var downloader = _downloader; // read here: the task below runs outside the lock
            foreach (var entry in _entries.Where(e => e.State == QueueState.Waiting && e is { IsRunning: false, Removing: false }).ToList())
            {
                if (_held || running >= _filesAtOnce)
                    break;
                started.Add(entry.MoveTo(QueueState.Resolving));
                entry.Stop = new CancellationTokenSource();
                entry.StopAs = QueueState.Paused;
                var stop = entry.Stop.Token;
                entry.Run = Task.Run(() => RunAsync(entry, downloader, stop)); // outside Pump's lock, and off the thread that called it, which may be the UI's
                running++;
            }
            var idle = running == 0 && (_held || !_entries.Any(e => e.State == QueueState.Waiting));
            if (idle)
                _idle.TrySetResult();
            else if (_idle.Task.IsCompleted)
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Raise(started);
    }

    private async Task RunAsync(Entry entry, SegmentedDownload downloader, CancellationToken stop)
    {
        DownloadRequest request;
        lock (_lock)
            request = new DownloadRequest(entry.Link, entry.Folder, entry.Name, entry.Limit, entry.Details, entry.FolderChosen, entry.Sha256);
        QueueState final;
        (string? Path, string? Error, Stage? Stage, string? Sha256) outcome = (null, null, null, null);
        try
        {
            var result = await downloader.RunAsync(request, e => OnDownload(entry, e), stop);
            final = result.Skipped ? QueueState.Skipped : QueueState.Done;
            outcome.Path = result.Path;
            outcome.Sha256 = result.Sha256;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            lock (_lock)
                final = entry.StopAs;
        }
        catch (StageException e)
        {
            (final, outcome.Error, outcome.Stage) = (QueueState.Failed, e.Message, e.Stage);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            (final, outcome.Error, outcome.Stage) = (QueueState.Failed, $"The file could not be saved: {e.Message}", Stage.Download);
        }
#pragma warning disable CA1031 // one file's unexpected failure must not stop the queue; it is shown on the file
        catch (Exception e)
#pragma warning restore CA1031
        {
            (final, outcome.Error, outcome.Stage) = (QueueState.Failed, $"Something went wrong: {e.Message}", null);
        }
        QueueEvent changed;
        lock (_lock)
        {
            entry.Stop?.Dispose();
            (entry.Stop, entry.Run) = (null, null);
            entry.Path = outcome.Path ?? entry.Path;
            entry.Sha256 = outcome.Sha256 ?? entry.Sha256;
            (entry.Error, entry.FailedAt) = (outcome.Error, outcome.Stage);
            (entry.BytesPerSecond, entry.Connections) = (0, 0);
            if (final == QueueState.Done && entry.Total is { } total)
                entry.Done = total;
            changed = entry.MoveTo(final);
        }
        Raise([changed]);
        Pump();
    }

    private void OnDownload(Entry entry, DownloadEvent e)
    {
        QueueEvent? changed;
        lock (_lock)
        {
            changed = e switch
            {
                DownloadStarted started => entry.Started(started),
                DownloadProgressed progress => entry.Update(progress.Done, progress.Total, progress.BytesPerSecond, progress.Connections),
                DownloadRetrying => entry.Retried(),
                DownloadConnectionsReduced reduced => entry.Update(entry.Done, entry.Total, entry.BytesPerSecond, reduced.Left),
                DownloadRestarted => entry.Update(0, entry.Total, 0, entry.Connections),
                DownloadSkipped skipped => entry.Update(entry.Done, entry.Total, 0, 0, skipped.Path),
                _ => null,
            };
        }
        if (changed is not null)
            Raise([changed]);
    }

    private void Raise(IEnumerable<QueueEvent> events) => QueueListeners.Raise(Changed, events, _log);

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>A file in the queue; its fields change only under the queue's lock.</summary>
    private sealed class Entry(Guid id, FileLink link, string folder)
    {
        private long _revision;

        public Guid Id { get; } = id;
        public FileLink Link { get; } = link;
        public string Folder { get; } = folder;
        public string? Name { get; set; }
        public RequestDetails Details { get; set; } = RequestDetails.None;
        public bool FolderChosen { get; init; }
        public string? Sha256 { get; set; }
        public QueueState State { get; set; } = QueueState.Waiting;
        public string? Path { get; set; }
        public long Done { get; set; }
        public long? Total { get; set; }
        public double BytesPerSecond { get; set; }
        public int Connections { get; set; }
        public int Retries { get; set; }
        public string? Error { get; set; }
        public Stage? FailedAt { get; set; }
        public SpeedLimit Limit { get; } = new();

        public CancellationTokenSource? Stop { get; set; }
        public Task? Run { get; set; }

        /// <summary>The state a stop request leaves the file in once its download has stopped.</summary>
        public QueueState StopAs { get; set; }

        /// <summary>Being removed: it stops, and nothing starts it again.</summary>
        public bool Removing { get; set; }

        public bool IsRunning => Run is not null;

        public QueueItem Snapshot() => new(Id, Link, Folder, Name, State, Path, Done, Total, BytesPerSecond, Connections, Retries,
                                           Limit.BytesPerSecond, Error, FailedAt, _revision, FolderChosen, Sha256);

        /// <summary>A snapshot after a change, newer than any before it.</summary>
        public QueueItem Next()
        {
            _revision++;
            return Snapshot();
        }

        public QueueItemStateChanged MoveTo(QueueState state)
        {
            var was = State;
            State = state;
            return new QueueItemStateChanged(Next(), was);
        }

        public QueueEvent Started(DownloadStarted started)
        {
            (Path, Done, Total, Connections) = (started.Path, started.Done, started.Total, started.Connections);
            return State == QueueState.Downloading ? new QueueItemUpdated(Next()) : MoveTo(QueueState.Downloading);
        }

        public QueueItemUpdated Update(long done, long? total, double speed, int connections, string? path = null)
        {
            (Done, Total, BytesPerSecond, Connections, Path) = (done, total, speed, connections, path ?? Path);
            return new QueueItemUpdated(Next());
        }

        public QueueItemUpdated Retried()
        {
            Retries++;
            return new QueueItemUpdated(Next());
        }
    }
}
