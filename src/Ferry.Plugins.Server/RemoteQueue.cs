using Ferry.Core;
using System.Diagnostics.CodeAnalysis;
using Ferry.Core.Browse;
using Ferry.Core.Download;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.AfterDownload;

namespace Ferry.Plugins.Server;

/// <summary>
/// Server mode's queue on the PC. Files download on the server (its agent runs a <see cref="DownloadQueue"/>); this
/// mirrors that queue, forwards requests to it, and brings each finished file over (<see cref="FileTransfer"/>) into
/// the download folder. Then the server forgets the file and deletes it, unless told to keep it. The connection can
/// come and go (<see cref="AttachAsync"/>): the server carries on meanwhile, and the PC catches up when it is back.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "It is a queue of downloads, though not a Queue<T>.")]
public sealed class RemoteQueue : IDownloadQueue
{
    private readonly Lock _lock = new();
    private readonly List<View> _views = [];
    private readonly Dictionary<Guid, bool> _forget = []; // removed here while the server still has them: id → keep file
    private readonly Dictionary<Guid, long> _gone = []; // removed by the server: id → that revision, so older news can't bring it back
    private readonly Dictionary<Guid, RequestDetails> _details = []; // what unfinished files were added with, to send again after the agent restarts
    private readonly IRemoteFiles _files;
    private readonly SpeedLimit _transferLimit;
    private readonly Action<string, Exception>? _log;
    private volatile FerrySettings _settings;
    private AgentClient? _client;
    private int _session;
    private bool _held;
    private bool _synced; // a hello has said what the server has: an item with no view is one the server no longer has
    private TaskCompletionSource _idle = Completed();
    private TaskCompletionSource _collected = Completed();

    // an agent that doesn't answer at all (hung, or a connection gone quiet) is a failure to show, not a wait for ever
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FileInfoTimeout = TimeSpan.FromMinutes(15);

    // the agent asks the file's site, with retries (LinkProbe.AskRetry takes about 160 s to give up), before it can answer
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    // a folder of many files, each asked about with retries, takes longer than an ordinary request may
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(10);

    /// <param name="settings">sent to the agent, and used for the transfers</param>
    /// <param name="files">reads finished files on the server (the agent, over ssh)</param>
    /// <param name="requests">the request details saved from last time (<see cref="RequestStore"/>), by item</param>
    /// <param name="log">where a listener's failure, or a line from the agent that isn't a message, is noted</param>
    public RemoteQueue(FerrySettings settings, IRemoteFiles files, IReadOnlyDictionary<Guid, RequestDetails>? requests = null,
                       Action<string, Exception>? log = null)
    {
        (_settings, _files, _log) = (settings, files, log);
        _transferLimit = new SpeedLimit(settings.TransferLimit);
        foreach (var (id, details) in requests ?? new Dictionary<Guid, RequestDetails>())
            _details[id] = details;
    }

    public event Action<QueueEvent>? Changed;

    /// <summary>The connection to the agent came (true) or went (false); raised on a background thread.</summary>
    public event Action<bool>? ConnectionChanged;


    public bool IsAttached
    {
        get
        {
            lock (_lock)
                return _client is not null;
        }
    }

    /// <summary>The agent's build, from the last hello; null before the first attach.</summary>
    public string? AgentVersion { get; private set; }

    /// <summary>Every album the agent watches: on each attach and after every change; raised on a background thread.</summary>
    internal event Action<IReadOnlyList<WatchedAlbum>>? WatchedChanged;

    /// <summary>
    /// The secrets of the plugins installed on the server, by name, to go with the settings: over SSH, to the agent's memory.
    /// Set before connecting and before <see cref="ReconfigureAsync"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> PluginSecrets { get; set; } = new Dictionary<string, string>();

    /// <summary>The plugins installed on the server, running or not, from the last hello.</summary>
    public IReadOnlyList<AgentPlugin> Plugins { get; private set; } = [];

    /// <summary>Free space in the server's download folder, or null when the agent can't tell.</summary>
    /// <exception cref="StageException">not connected (<see cref="Stage.Server"/>)</exception>
    public async Task<long?> FreeSpaceAsync() => (await RequestAsync(new AgentRequest(0, AgentCommand.Space))).FreeBytes;

    /// <summary>How the agent and its server are doing now.</summary>
    /// <exception cref="StageException">not connected (<see cref="Stage.Server"/>)</exception>
    public async Task<AgentStats?> StatsAsync() => (await RequestAsync(new AgentRequest(0, AgentCommand.Stats))).Stats;

    /// <summary>The end of the agent's log on the server.</summary>
    /// <exception cref="StageException">not connected (<see cref="Stage.Server"/>)</exception>
    public async Task<string> LogAsync() => (await RequestAsync(new AgentRequest(0, AgentCommand.Log))).Log ?? "";

    public IReadOnlyList<QueueItem> Items
    {
        get
        {
            lock (_lock)
                return [.. _views.Select(v => v.Shown).OfType<QueueItem>()];
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
    /// Completes when the server has heard of every file brought over so far. Until then it still offers them, so a
    /// process that stops first would bring them over again next time.
    /// </summary>
    public Task WhenCollected
    {
        get
        {
            lock (_lock)
                return _collected.Task;
        }
    }

    public QueueItem? Find(Guid id)
    {
        lock (_lock)
            return Get(id)?.Shown;
    }

    /// <summary>
    /// Use new settings: the transfers follow them from now on, and the agent is told, when it is connected (else the
    /// next hello carries them). The agent's answer says when they can't be used there.
    /// </summary>
    /// <exception cref="InvalidOperationException">the agent refused them, saying why</exception>
    public async Task ReconfigureAsync(FerrySettings settings)
    {
        _settings = settings;
        _transferLimit.BytesPerSecond = settings.TransferLimit;
        Pump();
        try
        {
            await RequestAsync(new AgentRequest(0, AgentCommand.Settings, Settings: settings.Changed(), Secrets: new(PluginSecrets)));
        }
        catch (StageException e) when (e.Stage == Stage.Server && e.IsRetryable)
        {
            // not connected, or the connection is going: the hello of the next attach sends them all
        }
    }

    /// <summary>
    /// Hold the transfers, or let them go: while held no file starts coming over (one already coming finishes), as the
    /// schedule says for this PC. The server keeps downloading: the schedule is about this PC's connection.
    /// </summary>
    public Task SetHeldAsync(bool held)
    {
        lock (_lock)
            _held = held;
        Pump();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The request details of files that may still need them, to save apart from the queue. Those of files that are done,
    /// or that the server no longer has, are let go; until the server has been heard from, all are kept.
    /// </summary>
    public Dictionary<Guid, RequestDetails> RequestsToKeep()
    {
        lock (_lock)
        {
            foreach (var id in _details.Keys.Where(id => Get(id) is { } view ? view.Shown?.State is QueueState.Done or QueueState.Skipped : _synced).ToList())
                _details.Remove(id);
            return new Dictionary<Guid, RequestDetails>(_details);
        }
    }

    /// <summary>The files behind a link to many, read on the server; null when it is a link to one file.</summary>
    /// <exception cref="StageException">not connected (<see cref="Stage.Server"/>), or the server couldn't read them</exception>
    public async Task<FileList?> ExpandAsync(Uri link, RequestDetails details, CancellationToken cancel) =>
        (await ReadOnServerAsync(new AgentRequest(0, AgentCommand.Expand, Link: link.ToString(), Details: details), ListTimeout, cancel)).Files;

    /// <summary>A file's name and size as its site gives them, read on the server, before it is added.</summary>
    /// <exception cref="StageException">not connected (<see cref="Stage.Server"/>), or the server couldn't read it</exception>
    public async Task<(string Name, long? Size)> ReadAsync(FileLink link, RequestDetails details, CancellationToken cancel)
    {
        var answer = await ReadOnServerAsync(new AgentRequest(0, AgentCommand.Read, Link: link.Original.ToString(), Details: details), ReadTimeout, cancel);
        return (answer.Name ?? throw new StageException(Stage.Server, "The server answered without the file's name."), answer.Size);
    }

    /// <summary>Ask the agent to read something; its refusal is a failure to read, for the user like any other.</summary>
    private async Task<AgentMessage> ReadOnServerAsync(AgentRequest request, TimeSpan timeout, CancellationToken cancel)
    {
        try
        {
            return await RequestAsync(request, timeout, cancel);
        }
        catch (InvalidOperationException e)
        {
            throw new StageException(Stage.Server, e.Message, inner: e);
        }
    }

    /// <summary>
    /// Talk to the agent over <paramref name="fromAgent"/> and <paramref name="toAgent"/> (the SSH channel, or a
    /// socket): say hello with this PC's version and settings, then take over the server's queue as it is now.
    /// </summary>
    /// <exception cref="AgentVersionException">the agent is another version, and must be replaced first</exception>
    /// <exception cref="StageException">the agent didn't answer, or refused (<see cref="Stage.Server"/>)</exception>
    public async Task AttachAsync(Stream fromAgent, Stream toAgent, CancellationToken cancel = default)
    {
        await DetachAsync();
        int session;
        lock (_lock)
        {
            session = ++_session;
            _gone.Clear(); // news of another session is dropped anyway, and its hello says what the server has
        }
        var client = new AgentClient(fromAgent, toAgent, m => OnAgentEvent(session, m), () => OnClosed(session), _log);
        AgentMessage hello;
        try
        {
            hello = await AskAsync(client, new AgentRequest(0, AgentCommand.Hello, Version: Product.Build,
                                                            Settings: _settings.Changed(), Secrets: new(PluginSecrets)), AnswerTimeout, cancel);
            if (hello.Version is { } version && version != Product.Build)
                throw new AgentVersionException(Product.Build, version);
            if (hello.Error is { } error)
                throw new StageException(Stage.Server, error);
        }
        catch
        {
            await DisposeQuietlyAsync(client);
            throw;
        }
        var events = new List<QueueEvent>();
        List<(View View, Guid Id)> collects;
        Dictionary<Guid, bool> forget;
        Dictionary<Guid, RequestDetails> details;
        bool dead;
        lock (_lock)
        {
            // closed between its hello and now: OnClosed has been and gone, so it must not become the client
            dead = client.IsClosed || session != _session;
            if (dead)
            {
                (collects, forget, details) = ([], [], []);
            }
            else
            {
                _client = client;
                AgentVersion = hello.Version;
                Plugins = hello.Plugins ?? [];
                Sync(hello.Items ?? [], session, events);
                foreach (var view in _views.Where(v => v.Overlay is { State: QueueState.Failed, RetryOnAttach: true }))
                {
                    view.Overlay!.State = QueueState.OnServer;
                    (view.Overlay.Error, view.Overlay.FailedAt, view.Overlay.RetryOnAttach) = (null, null, false);
                    Publish(view, events);
                }
                collects = [.. _views.Where(v => v.Overlay is { CollectPending: true }).Select(v => (v, v.Id))];
                forget = new Dictionary<Guid, bool>(_forget);
                details = RequestsToKeep();
            }
        }
        if (dead)
        {
            await DisposeQuietlyAsync(client);
            throw new StageException(Stage.Server, "The connection to the server was lost.", isRetryable: true);
        }
        Raise(events);
        WatchedChanged?.Invoke(hello.Albums ?? []);
        _ = HeartbeatAsync(client);
        ConnectionChanged?.Invoke(true);
        bool lost;
        lock (_lock)
            lost = _client != client;
        if (lost)
            ConnectionChanged?.Invoke(false); // it closed while that was said: the last word is the true one
        foreach (var (view, _) in collects)
            await CollectAsync(view);
        foreach (var (id, keep) in forget)
            await ForgetAsync(id, keep);
        foreach (var (id, kept) in details)
            await ResendDetailsAsync(id, kept);
        Pump();
    }

    /// <summary>
    /// Give the agent a file's request details again: it keeps them in memory only, so after a restart it has none.
    /// An agent that still has them keeps them. If the connection goes meanwhile, the next attach sends them again.
    /// </summary>
    private async Task ResendDetailsAsync(Guid id, RequestDetails details)
    {
        try
        {
            await RequestAsync(new AgentRequest(0, AgentCommand.Details, Item: id, Details: details));
        }
        catch (StageException e) when (e.Stage == Stage.Server && e.IsRetryable)
        {
            // not connected any more: the next attach sends them
        }
        catch (InvalidOperationException)
        {
            // the server no longer has the file
        }
    }

    /// <summary>Close the connection to the agent; the server carries on, and transfers stop until the next attach.</summary>
    public async Task DetachAsync()
    {
        AgentClient? client;
        lock (_lock)
        {
            (client, _client) = (_client, null);
            _session++;
        }
        if (client is null)
            return;
        await DisposeQuietlyAsync(client);
        ConnectionChanged?.Invoke(false);
    }

    /// <param name="options">all of them go to the server but <see cref="AddOptions.Folder"/>, which is a place on this PC:
    /// files come to the download folder, under <see cref="AddOptions.Album"/>. Cookies and headers go with the request, over
    /// SSH, and the agent keeps them in memory only</param>
    public async Task<QueueAddResult> AddAsync(IEnumerable<string> lines, AddOptions? options = null)
    {
        var answer = await RequestAsync(new AgentRequest(0, AgentCommand.Add, Lines: [.. lines], Name: options?.Name, Folder: options?.Album,
                                                         Details: options?.Details, Flag: options?.Paused, Sha256: options?.Sha256));
        var events = new List<QueueEvent>();
        List<QueueItem> added, duplicates;
        lock (_lock)
        {
            added = [.. (answer.Items ?? []).Select(i => FromServer(i, _session, events).Shown!)];
            duplicates = [.. (answer.Duplicates ?? []).Select(i => FromServer(i, _session, events).Shown!)];
            if (options?.Details is { IsEmpty: false } details)
            {
                foreach (var item in added)
                    _details[item.Id] = details;
            }
        }
        Raise(events);
        Pump();
        return new QueueAddResult(added, duplicates, answer.Rejected ?? []);
    }

    public async Task PauseAsync(Guid id)
    {
        if (!await StopHereAsync(id, QueueState.Paused, QueueState.OnServer))
            await ForwardAsync(AgentCommand.Pause, id);
    }

    public async Task CancelAsync(Guid id)
    {
        if (!await StopHereAsync(id, QueueState.Cancelled, QueueState.OnServer, QueueState.Paused))
            await ForwardAsync(AgentCommand.Cancel, id);
    }

    public async Task ResumeAsync(Guid id)
    {
        if (!RequeueHere(id, QueueState.Paused))
            await ForwardAsync(AgentCommand.Resume, id);
    }

    public async Task RetryAsync(Guid id)
    {
        if (!RequeueHere(id, QueueState.Failed, QueueState.Cancelled))
            await ForwardAsync(AgentCommand.Retry, id);
    }

    public async Task RemoveAsync(Guid id, bool deleteParts)
    {
        View? view;
        lock (_lock)
            view = Get(id);
        if (view is null)
            return;
        if (view.Overlay is null)
        {
            await RequestAsync(new AgentRequest(0, AgentCommand.Remove, Item: id, Flag: deleteParts));
        }
        else
        {
            lock (_lock)
                view.Overlay.Removing = true;
            await StopHereAsync(id, QueueState.Cancelled, QueueState.OnServer, QueueState.Paused);
            var overlay = view.Overlay;
            if (deleteParts && overlay is { Path: { } part, State: not (QueueState.Done or QueueState.Skipped) })
                PartFile.TryDelete(part); // removed from the queue either way
            if (overlay.State is not (QueueState.Done or QueueState.Skipped) || overlay.CollectPending)
            {
                lock (_lock)
                    _forget[id] = _settings.KeepServerFiles;
                await ForgetAsync(id, _settings.KeepServerFiles);
            }
        }
        QueueItem? removed;
        lock (_lock)
        {
            _details.Remove(id);
            removed = _views.Remove(view) ? Next(view) : null;
        }
        if (removed is not null)
            Raise([new QueueItemRemoved(removed)]);
        Pump();
    }

    public Task RenameAsync(Guid id, string? name) =>
        RequestAsync(new AgentRequest(0, AgentCommand.Rename, Item: id, Name: name));

    public Task SetSpeedLimitAsync(Guid id, long bytesPerSecond) =>
        RequestAsync(new AgentRequest(0, AgentCommand.SetLimit, Item: id, BytesPerSecond: bytesPerSecond));

    /// <summary>Pause the transfers here, then everything on the server.</summary>
    public async Task PauseAllAsync()
    {
        await Task.WhenAll(Ids().Select(id => StopHereAsync(id, QueueState.Paused, QueueState.OnServer)));
        await RequestAsync(new AgentRequest(0, AgentCommand.PauseAll));
    }

    public async Task ResumeAllAsync()
    {
        foreach (var id in Ids())
            RequeueHere(id, QueueState.Paused);
        await RequestAsync(new AgentRequest(0, AgentCommand.ResumeAll));
    }

    /// <summary>Stop the transfers (what arrived is kept) and the connection; the server carries on.</summary>
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(Ids().Select(id => StopHereAsync(id, QueueState.Paused, QueueState.OnServer)));
        await DetachAsync();
    }

    private View? Get(Guid id) => _views.Find(v => v.Id == id);

    private List<Guid> Ids()
    {
        lock (_lock)
            return [.. _views.Select(v => v.Id)];
    }

    private async Task ForwardAsync(AgentCommand command, Guid id) =>
        await RequestAsync(new AgentRequest(0, command, Item: id));

    /// <summary>Send a request to the agent; an error in its answer becomes an exception.</summary>
    /// <param name="timeout">how long the agent may take to answer; a minute unless the request is a long one</param>
    /// <exception cref="StageException">not connected, the connection was lost, or the agent failed at a stage</exception>
    /// <exception cref="InvalidOperationException">the agent refused, saying why</exception>
    internal async Task<AgentMessage> RequestAsync(AgentRequest request, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        AgentClient client;
        lock (_lock)
            client = _client ?? throw new StageException(Stage.Server, "Not connected to the server.", isRetryable: true);
        var answer = await AskAsync(client, request, timeout ?? AnswerTimeout, cancel);
        return answer.Error switch
        {
            null => answer,
            var error when answer.ErrorStage is { } stage => throw new StageException(stage, error),
            var error => throw new InvalidOperationException(error),
        };
    }

    /// <summary>
    /// How often the agent is asked something small while attached. A connection can go quiet without closing (the PC
    /// slept, the network changed, a router forgot it), and then no news comes and nothing says why.
    /// </summary>
    internal TimeSpan HeartbeatEvery { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long the agent has to answer a heartbeat before the connection counts as dead and is dropped.</summary>
    internal TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(30);

    private static async Task<AgentMessage> AskAsync(AgentClient client, AgentRequest request, TimeSpan timeout, CancellationToken cancel)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        wait.CancelAfter(timeout);
        var sending = client.SendAsync(request, wait.Token);
        try
        {
            // WaitAsync too: a write into a connection gone quiet may not heed the token
            return await sending.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            _ = sending.ContinueWith(t => t.Exception, TaskScheduler.Default); // its own failure, later, is no news
            throw new StageException(Stage.Server,
                $"The server's agent didn't answer within {timeout.TotalSeconds:0} s. Its log is {AgentPaths.LogOnServer} on the server.",
                isRetryable: true);
        }
    }

    /// <summary>Ask the agent something small now and then; no answer in time drops the connection, so it is replaced.</summary>
    private async Task HeartbeatAsync(AgentClient client)
    {
        while (true)
        {
            await Task.Delay(HeartbeatEvery);
            lock (_lock)
            {
                if (_client != client)
                    return; // detached, or replaced: that one has its own
            }
            try
            {
                await AskAsync(client, new AgentRequest(0, AgentCommand.Ping), HeartbeatTimeout, CancellationToken.None);
            }
            catch (StageException) when (client.IsClosed)
            {
                return; // it closed meanwhile: that was noticed already
            }
            catch (StageException)
            {
                await DropAsync(client);
                return;
            }
        }
    }

    /// <summary>Give up on <paramref name="client"/> as if it had closed: the session reconnects.</summary>
    private async Task DropAsync(AgentClient client)
    {
        lock (_lock)
        {
            if (_client != client)
                return;
            _client = null;
            _session++;
        }
        ConnectionChanged?.Invoke(false);
        Pump();
        await DisposeQuietlyAsync(client);
    }

    /// <summary>Close a client without waiting long: closing a connection gone quiet can hang until SSH gives up on it.</summary>
    private static async Task DisposeQuietlyAsync(AgentClient client)
    {
        try
        {
            await client.DisposeAsync().AsTask().WaitAsync(DisposeWait);
        }
        catch (TimeoutException)
        {
            // it closes with the SSH connection, which the session replaces
        }
    }

    /// <summary>
    /// Take over the server's queue from a hello, as it is now: an agent that restarted counts its revisions from 0
    /// again, so its items replace what is here whatever their revision, unless this very session heard newer of them
    /// already (an event the agent sent right after its answer), and unless it told this session they are gone since.
    /// Items gone there go here too.
    /// </summary>
    private void Sync(List<RemoteItem> items, int session, List<QueueEvent> events)
    {
        _synced = true;
        var present = new HashSet<Guid>();
        foreach (var item in items.Where(i => !_forget.ContainsKey(i.Id) && !IsGone(i)))
        {
            FromServer(item, session, events, snapshot: true);
            present.Add(item.Id);
        }
        foreach (var view in _views.Where(v => !present.Contains(v.Id) && v.Session != session
                                               && v.Overlay is not { State: QueueState.Done or QueueState.Skipped }).ToList())
        {
            _views.Remove(view);
            _ = view.Overlay?.Stop?.CancelAsync();
            events.Add(new QueueItemRemoved(Next(view)));
        }
    }

    /// <summary>News older than the server's word that the item was removed: the agent sends events outside its queue's lock, so it can come after.</summary>
    private bool IsGone(RemoteItem item) => _gone.TryGetValue(item.Id, out var goneAt) && item.Revision <= goneAt;

    /// <summary>Note the server's latest about an item (ignoring older news), and start bringing it over once done.</summary>
    /// <param name="snapshot">from a hello: the agent's state now, newer than anything heard before this session</param>
    private View FromServer(RemoteItem remote, int session, List<QueueEvent> events, bool snapshot = false)
    {
        var item = remote.ToItem();
        var view = Get(item.Id);
        if (view is null)
        {
            view = new View(item);
            _views.Add(view);
        }
        else if (item.Revision < view.Server.Revision && (!snapshot || view.Session == session))
        {
            return view;
        }
        (view.Server, view.Session) = (item, session);
        if (item.State == QueueState.Done && view.Overlay is null)
            view.Overlay = new Overlay { State = QueueState.OnServer };
        Publish(view, events);
        return view;
    }

    private void OnAgentEvent(int session, AgentMessage message)
    {
        if (message.Event == AgentEventKind.WatchedChanged)
        {
            bool current;
            lock (_lock)
                current = session == _session;
            if (current)
                WatchedChanged?.Invoke(message.Albums ?? []);
            return;
        }
        var events = new List<QueueEvent>();
        lock (_lock)
        {
            if (session != _session || message.Item is not { } item)
                return;
            if (message.Event != AgentEventKind.Removed)
            {
                if (!_forget.ContainsKey(item.Id) && !IsGone(item))
                    FromServer(item, session, events);
            }
            else
            {
                _forget.Remove(item.Id);
                _details.Remove(item.Id);
                _gone[item.Id] = item.Revision;
                // a file brought over stays here as done; anything else the server dropped goes here too
                if (Get(item.Id) is { Overlay: not { State: QueueState.Done or QueueState.Skipped } } view)
                {
                    _views.Remove(view);
                    _ = view.Overlay?.Stop?.CancelAsync();
                    events.Add(new QueueItemRemoved(Next(view)));
                }
            }
        }
        Raise(events);
        Pump();
    }

    private void OnClosed(int session)
    {
        lock (_lock)
        {
            if (session != _session)
                return;
            _client = null;
        }
        ConnectionChanged?.Invoke(false);
        Pump();
    }

    /// <summary>Stop a file's transfer, or a file waiting to be brought over; false if it is still on the server's side.</summary>
    private async Task<bool> StopHereAsync(Guid id, QueueState to, params QueueState[] from)
    {
        Task? running = null;
        var events = new List<QueueEvent>();
        lock (_lock)
        {
            if (Get(id) is not { Overlay: { } overlay } view)
                return false;
            if (overlay.Run is not null)
            {
                overlay.StopAs = to;
                _ = overlay.Stop!.CancelAsync();
                running = overlay.Run;
            }
            else if (from.Contains(overlay.State))
            {
                overlay.State = to;
                Publish(view, events);
            }
        }
        Raise(events);
        if (running is not null)
            await running;
        Pump();
        return true;
    }

    /// <summary>Put a file back in line for its transfer; false if it is still on the server's side.</summary>
    private bool RequeueHere(Guid id, params QueueState[] from)
    {
        var events = new List<QueueEvent>();
        lock (_lock)
        {
            if (Get(id) is not { Overlay: { } overlay } view)
                return false;
            if (overlay.Removing)
                return true;
            if (overlay.Run is null && from.Contains(overlay.State))
            {
                overlay.State = QueueState.OnServer;
                (overlay.Error, overlay.FailedAt, overlay.RetryOnAttach) = (null, null, false);
                Publish(view, events);
            }
            else if (overlay is { Run: not null, Stop.IsCancellationRequested: true } && from.Contains(overlay.StopAs))
            {
                overlay.StopAs = QueueState.OnServer; // asked back while still stopping: it ends in line again
            }
        }
        Raise(events);
        Pump();
        return true;
    }

    /// <summary>Start transfers while there are free slots and a connection, and note whether the queue is idle.</summary>
    private void Pump()
    {
        var events = new List<QueueEvent>();
        lock (_lock)
        {
            if (_client is { } client && !_held)
            {
                var running = _views.Count(v => v.Overlay?.Run is not null);
                foreach (var view in _views.Where(v => v.Overlay is { State: QueueState.OnServer, Run: null, Removing: false }).ToList())
                {
                    if (running >= _settings.TransfersAtOnce)
                        break;
                    var overlay = view.Overlay!;
                    overlay.State = QueueState.Transferring;
                    overlay.StopAs = QueueState.Paused;
                    overlay.Stop = new CancellationTokenSource();
                    overlay.Run = TransferAsync(view, client, overlay.Stop.Token);
                    Publish(view, events);
                    running++;
                }
            }
            NoteHowItStands();
        }
        Raise(events);
    }

    /// <summary>Note whether the queue is idle and whether the server has heard of what came over; under the lock.</summary>
    private void NoteHowItStands()
    {
        var collecting = _views.Exists(v => v.Overlay is { CollectPending: true });
        var busy = collecting || _views.Exists(v => v.Overlay is null
            ? v.Server.State is QueueState.Waiting or QueueState.Resolving or QueueState.Downloading
            : v.Overlay.Run is not null || v.Overlay.State is QueueState.OnServer or QueueState.Transferring);
        Settle(ref _idle, !busy);
        Settle(ref _collected, !collecting);
    }

    private static void Settle(ref TaskCompletionSource signal, bool settled)
    {
        if (settled)
            signal.TrySetResult();
        else if (signal.Task.IsCompleted)
            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task TransferAsync(View view, AgentClient client, CancellationToken stop)
    {
        await Task.Yield(); // run outside Pump's lock
        var overlay = view.Overlay!;
        QueueState final;
        (string? Path, string? Error, Stage? Stage, bool RetryOnAttach) outcome = (null, null, null, false);
        try
        {
            // the download's own hash answers this at once; a file from an older build is hashed first, which can take a while
            var info = await AskAsync(client, new AgentRequest(0, AgentCommand.FileInfo, Item: view.Id), FileInfoTimeout, stop);
            if (info.Error is { } refused)
                throw new StageException(info.ErrorStage ?? Stage.Transfer, refused);
            var file = new RemoteFile(view.Id, view.Server.Link.Key, info.Path!, info.Size ?? 0, info.Sha256 ?? "");
            var download = _settings.Download;
            var connections = Math.Clamp(download.Connections, 1, ServerConnection.MaxLanes);
            // a file the agent keeps in a folder of its own (an album's) comes to one of the same name here, and neither
            // sorting nor the rules for where files go are for it; nor are the rules for what they are called for one the user named
            var album = info.Folder is { Length: > 0 } own ? DownloadQueue.Into(_settings.DownloadFolder, own) : null;
            var folder = album ?? (download.SortByType ? FileKinds.SortedInto(_settings.DownloadFolder, file.Name) : _settings.DownloadFolder);
            var place = album is null && view.Server.Name is null ? AfterDownloadPlugin.PlaceFor(_settings) : null;
            var transfer = new FileTransfer(_files, new TransferOptions(download.Retry, download.StallTimeout, _settings.OnExisting,
                                                                        _transferLimit, connections, Place: place));
            var result = await transfer.RunAsync(file, folder, e => OnTransfer(view, e), stop);
            final = result.Skipped ? QueueState.Skipped : QueueState.Done;
            outcome.Path = result.Path;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            lock (_lock)
                final = overlay.StopAs;
        }
        catch (StageException e)
        {
            (final, outcome.Error, outcome.Stage) = (QueueState.Failed, e.Message, e.Stage);
            outcome.RetryOnAttach = e.IsRetryable || e.Stage == Stage.Server;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            (final, outcome.Error, outcome.Stage) = (QueueState.Failed, $"The file could not be saved: {e.Message}", Stage.Transfer);
        }
#pragma warning disable CA1031 // one transfer's unexpected failure must not leave it stuck; it is shown on the file
        catch (Exception e)
#pragma warning restore CA1031
        {
            (final, outcome.Error, outcome.Stage) = (QueueState.Failed, $"Something went wrong: {e.Message}", Stage.Transfer);
        }
        var events = new List<QueueEvent>();
        var collect = final is QueueState.Done or QueueState.Skipped;
        lock (_lock)
        {
            overlay.Stop?.Dispose();
            (overlay.Stop, overlay.Run) = (null, null);
            overlay.State = final;
            overlay.Path = outcome.Path ?? overlay.Path;
            (overlay.Error, overlay.FailedAt, overlay.RetryOnAttach) = (outcome.Error, outcome.Stage, outcome.RetryOnAttach);
            (overlay.BytesPerSecond, overlay.Connections) = (0, 0);
            overlay.CollectPending = collect;
            NoteHowItStands();
            if (final == QueueState.Done && overlay.Total is { } total)
                overlay.Done = total;
            if (_views.Contains(view)) // removed meanwhile: nothing more to say about it
                Publish(view, events);
        }
        Raise(events);
        if (collect)
            await CollectAsync(view);
        Pump();
    }

    /// <summary>Tell the server a file was brought over; if that fails it is told at the next attach.</summary>
    private async Task CollectAsync(View view)
    {
        try
        {
            await RequestAsync(new AgentRequest(0, AgentCommand.Collected, Item: view.Id, Flag: _settings.KeepServerFiles));
        }
        catch (StageException e) when (e.Stage == Stage.Server && e.IsRetryable)
        {
            return; // not connected: CollectPending stays, and the next attach sends it
        }
        catch (InvalidOperationException)
        {
            // the server no longer has it
        }
        lock (_lock)
            view.Overlay!.CollectPending = false;
    }

    /// <summary>Tell the server to drop a file removed here; if that fails it is told at the next attach.</summary>
    private async Task ForgetAsync(Guid id, bool keepFile)
    {
        try
        {
            await RequestAsync(new AgentRequest(0, AgentCommand.Collected, Item: id, Flag: keepFile));
        }
        catch (StageException e) when (e.Stage == Stage.Server && e.IsRetryable)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            // the server no longer has it
        }
        lock (_lock)
            _forget.Remove(id);
    }

    private void OnTransfer(View view, DownloadEvent e)
    {
        var events = new List<QueueEvent>();
        lock (_lock)
        {
            var overlay = view.Overlay!;
            switch (e)
            {
                case DownloadStarted started:
                    (overlay.Path, overlay.Done, overlay.Total, overlay.Connections) = (started.Path, started.Done, started.Total, 1);
                    break;
                case DownloadProgressed progress:
                    (overlay.Done, overlay.Total, overlay.BytesPerSecond, overlay.Connections) =
                        (progress.Done, progress.Total, progress.BytesPerSecond, progress.Connections);
                    break;
                case DownloadRetrying:
                    overlay.Retries++;
                    break;
                case DownloadRestarted:
                    overlay.Done = 0;
                    break;
                case DownloadSkipped skipped:
                    overlay.Path = skipped.Path;
                    break;
                default:
                    return;
            }
            if (_views.Contains(view)) // removed meanwhile: nothing more to say about it
                Publish(view, events);
        }
        Raise(events);
    }

    /// <summary>Work out what the item looks like now, and note the event if that changed.</summary>
    private static void Publish(View view, List<QueueEvent> events)
    {
        var server = view.Server;
        var overlay = view.Overlay;
        var shown = overlay is null
            ? server
            : server with
            {
                State = overlay.State,
                Path = overlay.Path ?? server.Path,
                Done = overlay.Path is null ? server.Done : overlay.Done,
                Total = overlay.Total ?? server.Total,
                BytesPerSecond = overlay.BytesPerSecond,
                Connections = overlay.Connections,
                Retries = server.Retries + overlay.Retries,
                Error = overlay.Error,
                FailedAt = overlay.FailedAt,
            };
        var before = view.Shown;
        if (before is not null && shown with { Revision = before.Revision } == before)
            return;
        view.Shown = shown with { Revision = view.Revision + 1 };
        view.Revision++;
        events.Add(before is null ? new QueueItemAdded(view.Shown)
                   : before.State != view.Shown.State ? new QueueItemStateChanged(view.Shown, before.State)
                   : new QueueItemUpdated(view.Shown));
    }

    /// <summary>The item's last snapshot, one revision on, for its removal.</summary>
    private static QueueItem Next(View view) =>
        (view.Shown ?? view.Server) with { Revision = ++view.Revision };

    private void Raise(List<QueueEvent> events) => QueueListeners.Raise(Changed, events, _log);

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>One file: the server's view of it, and this PC's transfer once the server has finished it.</summary>
    private sealed class View(QueueItem server)
    {
        public Guid Id { get; } = server.Id;
        public QueueItem Server { get; set; } = server;
        public Overlay? Overlay { get; set; }
        public QueueItem? Shown { get; set; }
        public long Revision { get; set; }

        /// <summary>The attach that last heard about it from the server.</summary>
        public int Session { get; set; }
    }

    /// <summary>The PC's part of a file finished on the server.</summary>
    private sealed class Overlay
    {
        /// <summary>Being removed: it stops, and nothing starts it again.</summary>
        public bool Removing { get; set; }

        public QueueState State { get; set; }
        public string? Path { get; set; }
        public long Done { get; set; }
        public long? Total { get; set; }
        public double BytesPerSecond { get; set; }
        public int Connections { get; set; }
        public int Retries { get; set; }
        public string? Error { get; set; }
        public Stage? FailedAt { get; set; }
        public CancellationTokenSource? Stop { get; set; }
        public Task? Run { get; set; }
        public QueueState StopAs { get; set; }

        /// <summary>Failed because the connection went: tried again when it is back.</summary>
        public bool RetryOnAttach { get; set; }

        /// <summary>Brought over, but the server has not been told yet.</summary>
        public bool CollectPending { get; set; }
    }
}
