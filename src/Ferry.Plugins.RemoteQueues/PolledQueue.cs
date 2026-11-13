using System.Diagnostics.CodeAnalysis;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Queue;
using Ferry.Core.Sites;

namespace Ferry.Plugins.RemoteQueues;

/// <summary>A download as the machine that has it reports it.</summary>
/// <param name="Link">what it is downloading, if that was said</param>
public sealed record RemoteTask(string Id, QueueState State, string? Path, long Done, long? Total, double BytesPerSecond, int Connections, string? Error, Uri? Link, string Folder);

/// <summary>
/// The queue as another machine has it: Ferry turns a link into the file's address, the machine does the rest, and Ferry reads
/// back how it goes every moment. The list is that machine's, so what it was asked by anything else shows here too.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "It is a queue of downloads, though not a Queue<T>.")]
public abstract class PolledQueue : IDownloadQueue
{
    private const string SiteId = "remote";
    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(2);

    private readonly Func<SiteCatalog?> _sites;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, QueueItem> _items = [];
    private readonly Dictionary<Guid, string> _ids = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _reading;
    private TaskCompletionSource _idle = Done();
    private long _revision;

    /// <param name="address">where the machine is</param>
    /// <param name="sites">the catalog in effect, which turns a link into the address it is given</param>
    protected PolledQueue(Uri address, Func<SiteCatalog?> sites) => (Address, _sites) = (address, sites);

    public event Action<QueueEvent>? Changed;

    /// <summary>The machine couldn't be reached, or can again (null).</summary>
    public event Action<string?>? ProblemChanged;

    public Uri Address { get; }

    /// <summary>What it is called in what the user is told.</summary>
    public abstract string Name { get; }

    protected CancellationToken Stopping => _stop.Token;

    public IReadOnlyList<QueueItem> Items
    {
        get
        {
            lock (_lock)
                return [.. _items.Values];
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

    /// <summary>Start reading. Apart from the constructor, as what a subclass sets has to be there first.</summary>
    public void Start() => _reading = ReadAllTheTimeAsync();

    public QueueItem? Find(Guid id)
    {
        lock (_lock)
            return _ids.TryGetValue(id, out var task) ? _items.GetValueOrDefault(task) : null;
    }

    public async Task<QueueAddResult> AddAsync(IEnumerable<string> lines, AddOptions? options = null)
    {
        var (rejected, added) = (new List<RejectedLink>(), new List<QueueItem>());
        var sites = _sites() ?? throw new InvalidOperationException("The queue isn't ready yet.");
        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            if (!sites.TryParse(line, out var link, out var reason))
            {
                rejected.Add(new RejectedLink(line, reason));
                continue;
            }
            try
            {
                var details = options?.Details ?? RequestDetails.None;
                var file = await sites.ResolveAsync(link, details, Stopping);
                if (file.Transport is not null || file.Cookie is not null)
                {
                    rejected.Add(new RejectedLink(line, $"{Name} can't fetch this kind of link. Turn off Remote Queues to download it here."));
                    continue;
                }
                var task = await AddFileAsync(file, details, options, Stopping);
                added.Add(Remember(task, new QueueItem(Guid.CreateVersion7(), link, options?.Folder ?? "", options?.Name, options is { Paused: true } ? QueueState.Paused : QueueState.Waiting,
                                                       null, 0, null, 0, 0, 0, 0, null, null)));
            }
            catch (StageException e)
            {
                rejected.Add(new RejectedLink(line, e.Message));
            }
        }
        return new QueueAddResult(added, [], rejected);
    }

    public Task PauseAsync(Guid id) => OnTaskAsync(id, PauseTaskAsync);

    public Task ResumeAsync(Guid id) => OnTaskAsync(id, ResumeTaskAsync);

    public Task CancelAsync(Guid id) => OnTaskAsync(id, StopTaskAsync);

    public async Task RetryAsync(Guid id)
    {
        if (Find(id) is not { } item)
            return;
        var sites = _sites() ?? throw new InvalidOperationException("The queue isn't ready yet.");
        var file = await sites.ResolveAsync(item.Link, RequestDetails.None, Stopping);
        var task = await AddFileAsync(file, RequestDetails.None, null, Stopping);
        var old = TaskOf(item);
        Forget(id);
        await TryAsync(() => ForgetTaskAsync(old, Stopping));
        Remember(task, item with { State = QueueState.Waiting, Error = null, FailedAt = null, Done = 0 });
    }

    public async Task RemoveAsync(Guid id, bool deleteParts)
    {
        if (Find(id) is not { } item)
            return;
        var task = TaskOf(item);
        if (item.State is QueueState.Waiting or QueueState.Downloading or QueueState.Paused)
            await TryAsync(() => StopTaskAsync(task, Stopping));
        await TryAsync(() => ForgetTaskAsync(task, Stopping));
        Forget(id);
        Changed?.Invoke(new QueueItemRemoved(item));
    }

    public Task RenameAsync(Guid id, string? name) =>
        throw new InvalidOperationException($"{Name} chooses the name when it is given the file, so it can't be changed here.");

    public Task SetSpeedLimitAsync(Guid id, long bytesPerSecond) =>
        OnTaskAsync(id, (task, cancel) => LimitTaskAsync(task, bytesPerSecond, cancel));

    public virtual async Task PauseAllAsync()
    {
        foreach (var item in Items.Where(item => item.State is QueueState.Waiting or QueueState.Downloading))
            await PauseAsync(item.Id);
    }

    public virtual async Task ResumeAllAsync()
    {
        foreach (var item in Items.Where(item => item.State == QueueState.Paused))
            await ResumeAsync(item.Id);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            if (_reading is not null)
                await _reading;
        }
        catch (OperationCanceledException)
        {
            // what stopping it does
        }
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Everything the machine has now.</summary>
    /// <exception cref="StageException">it couldn't be reached, or refused</exception>
    protected abstract Task<IReadOnlyList<RemoteTask>> ListAsync(CancellationToken cancel);

    /// <summary>Give it the file; what it calls the download.</summary>
    protected abstract Task<string> AddFileAsync(ResolvedFile file, RequestDetails details, AddOptions? options, CancellationToken cancel);

    protected abstract Task PauseTaskAsync(string task, CancellationToken cancel);

    protected abstract Task ResumeTaskAsync(string task, CancellationToken cancel);

    /// <summary>Stop it for good.</summary>
    protected abstract Task StopTaskAsync(string task, CancellationToken cancel);

    /// <summary>Take what is left of it out of the machine's list.</summary>
    protected abstract Task ForgetTaskAsync(string task, CancellationToken cancel);

    /// <summary>A limit on one download, where the machine has that.</summary>
    protected virtual Task LimitTaskAsync(string task, long bytesPerSecond, CancellationToken cancel) =>
        throw new InvalidOperationException($"{Name} can't limit one download.");

    private async Task ReadAllTheTimeAsync()
    {
        string? problem = null;
        while (!Stopping.IsCancellationRequested)
        {
            string? now = null;
            try
            {
                Apply(await ListAsync(Stopping));
            }
            catch (StageException e)
            {
                now = e.Message;
            }
            if (now != problem)
                ProblemChanged?.Invoke(problem = now);
            await Task.Delay(ReadEvery, Stopping);
        }
    }

    private void Apply(IReadOnlyList<RemoteTask> reported)
    {
        var events = new List<QueueEvent>();
        lock (_lock)
        {
            var gone = _items.Keys.ToHashSet();
            foreach (var task in reported)
            {
                gone.Remove(task.Id);
                _items.TryGetValue(task.Id, out var old);
                var now = Read(task, old);
                if (old == now)
                    continue;
                now = now with { Revision = ++_revision };
                _items[task.Id] = now;
                _ids[now.Id] = task.Id;
                events.Add(old is null ? new QueueItemAdded(now) : old.State != now.State ? new QueueItemStateChanged(now, old.State) : new QueueItemUpdated(now));
            }
            foreach (var task in gone)
            {
                if (_items.Remove(task, out var item))
                {
                    _ids.Remove(item.Id);
                    events.Add(new QueueItemRemoved(item));
                }
            }
            UpdateIdle();
        }
        foreach (var e in events)
            Changed?.Invoke(e);
    }

    /// <summary>An item as the machine reports it, keeping what only Ferry knows of it.</summary>
    private static QueueItem Read(RemoteTask task, QueueItem? old)
    {
        var link = old?.Link ?? new FileLink(SiteId, task.Link ?? new Uri($"https://remote.invalid/{task.Id}"), task.Id);
        return (old ?? new QueueItem(Guid.CreateVersion7(), link, task.Folder, null, task.State, task.Path, 0, null, 0, 0, 0, 0, null, null)) with
        {
            State = task.State,
            Path = task.Path ?? old?.Path,
            Done = task.Done,
            Total = task.Total,
            BytesPerSecond = task.BytesPerSecond,
            Connections = task.Connections,
            Error = task.Error,
            FailedAt = task.State == QueueState.Failed ? Stage.Download : null,
            Revision = old?.Revision ?? 0,
        };
    }

    private QueueItem Remember(string task, QueueItem item)
    {
        QueueItem kept;
        lock (_lock)
        {
            kept = item with { Revision = ++_revision };
            _items[task] = kept;
            _ids[kept.Id] = task;
            UpdateIdle();
        }
        Changed?.Invoke(new QueueItemAdded(kept));
        return kept;
    }

    private void Forget(Guid id)
    {
        lock (_lock)
        {
            if (_ids.Remove(id, out var task))
                _items.Remove(task);
            UpdateIdle();
        }
    }

    private string TaskOf(QueueItem item)
    {
        lock (_lock)
            return _ids.GetValueOrDefault(item.Id, "");
    }

    private void UpdateIdle()
    {
        var busy = _items.Values.Any(item => item.State is QueueState.Waiting or QueueState.Downloading);
        if (busy && _idle.Task.IsCompleted)
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        else if (!busy)
            _idle.TrySetResult();
    }

    private async Task OnTaskAsync(Guid id, Func<string, CancellationToken, Task> action)
    {
        if (Find(id) is { } item)
            await action(TaskOf(item), Stopping);
    }

    /// <summary>Asking something that the machine may have dealt with already: that it had isn't a failure.</summary>
    private static async Task TryAsync(Func<Task> ask)
    {
        try
        {
            await ask();
        }
        catch (StageException)
        {
            // not there any more
        }
    }

    private static TaskCompletionSource Done()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }
}
