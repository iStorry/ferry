using Ferry.Core.Download;
using Ferry.Core.Http;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.Official;
using Ferry.Plugins.RemoteQueues;
using Ferry.Plugins.Server;
using Microsoft.UI.Dispatching;

namespace Ferry.App.Services;

/// <summary>How the connection to where downloads happen is right now, as the sidebar's card shows it.</summary>
public enum ModeStatus
{
    Local,
    Connecting,
    Connected,

    /// <summary>The connection dropped and is being made again; it counts as offline only after a short while.</summary>
    Reconnecting,

    /// <summary>Closing the connection (the app is closing, or the mode changed); the server carries on.</summary>
    Disconnecting,

    Offline,
    NotSetUp,
}

/// <summary>Where downloads happen, and where every site is read from, whatever the connection is doing (<see cref="ModeStatus"/>).</summary>
public enum DownloadPlace
{
    ThisPc,

    /// <summary>Server mode: the server's agent downloads and reads every site, and files come over to this PC.</summary>
    Server,

    /// <summary>Remote Queues: another download manager (aria2, a Synology) downloads where it runs.</summary>
    RemoteQueue,
}

/// <summary>
/// The queue for the current mode, kept alive for the app's lifetime. Local mode: a <see cref="DownloadQueue"/> saved to
/// queue.json on every change and restored (paused) at start. Server mode: a <see cref="ServerSession"/>, connected in
/// the background and retried while the server can't be reached. Events come on the UI thread.
/// </summary>
public sealed class QueueHost(AppServices app, DispatcherQueue ui) : IAsyncDisposable
{
    private static readonly TimeSpan[] RetryAfter = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    // a dropped connection usually comes back within seconds (Wi-Fi, sleep): until then it is "reconnecting", not an error
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(10);

    // closing never waits longer for the server than this; ssh processes left over end with the app (their job)
    private static readonly TimeSpan DisconnectWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ScheduleCheck = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SaveAfter = TimeSpan.FromMilliseconds(500);
    private const string ThisPc = "This PC";
    private const string SomeServer = "your server"; // said in the middle of a sentence: "On your server, then here"
    private readonly SpeedLimit _overall = new();
    private static readonly TimeSpan QueueReadWait = TimeSpan.FromMilliseconds(250);
    private readonly QueueStore _store = new(app.QueueFile);
    private bool _queueUnread;
    private readonly RequestStore _requests = new(app.Secrets, CrashLog.Write);
    private readonly DispatcherQueueTimer _saveTimer = ui.CreateTimer();
    private readonly DispatcherQueueTimer _offlineTimer = ui.CreateTimer();
    private readonly DispatcherQueueTimer _scheduleTimer = ui.CreateTimer();
    private WhenDone _whenDone;
    private bool _held;
    private string? _reconnectProblem; // why the last reconnection try failed, shown once it counts as offline
    private readonly SemaphoreSlim _switching = new(1, 1);
    private CancellationTokenSource _stop = new();
    private HttpFetcher? _http;
    private HostConnections? _hosts;
    private int _hostsAllow; // the setting _hosts was made for
    private ServerSession? _session;
    private DownloadQueue? _local;
    private PolledQueue? _remote;
    private string? _remoteWhich;

    /// <summary>
    /// Turns links to many files into the files, where the download will happen, or null before <see cref="StartAsync"/>. In
    /// server mode the server reads them, after the plugins that must read here (<see cref="SitePlugin.ReadsListsHere"/>).
    /// </summary>
    public LinkExpander? Expander { get; private set; }

    /// <summary>The queue, or null while the server is being reached.</summary>
    public IDownloadQueue? Queue { get; private set; }

    /// <summary>Whether links can go in now: there is a queue, and in server mode it is connected.</summary>
    public bool IsReady => Queue is { } queue && (queue is not RemoteQueue remote || remote.IsAttached);

    /// <summary>Which links can be queued, or null before <see cref="StartAsync"/>.</summary>
    public SiteCatalog? Sites { get; private set; }

    public ModeStatus Status { get; private set; } = ModeStatus.Local;

    /// <summary>A line about <see cref="Status"/> for the user (the server, a failure).</summary>
    public string StatusDetail { get; private set; } = "";

    /// <summary>Where downloads happen in this mode, connected or not.</summary>
    public DownloadPlace Where { get; private set; } = DownloadPlace.ThisPc;

    /// <summary>What to call <see cref="Where"/>: "This PC", the server ("al@vps"), or the remote queue ("aria2 at nas.local").</summary>
    public string WhereDisplay { get; private set; } = ThisPc;

    /// <summary>Free space on the server, when connected and known.</summary>
    public long? ServerFreeBytes { get; private set; }

    /// <summary>The saved queue is there but couldn't be read, so the queue here isn't saved over it.</summary>
    public bool QueueUnread => _local is not null && _queueUnread;

    /// <summary>The plugins installed on the server, running or not, as it said when Ferry connected; none while it isn't.</summary>
    public IReadOnlyList<AgentPlugin> ServerPlugins => _session is { Queue.IsAttached: true } session ? session.Queue.Plugins : [];

    public string? ServerDisplay => _session?.Display ?? (ServerProfile.From(app.Settings) is { IsSet: true } profile ? profile.Display : null);

    /// <summary>A queue event, on the UI thread.</summary>
    public event Action<QueueEvent>? Changed;

    /// <summary>A different queue took over (the mode changed, or the server connected): rebuild the list.</summary>
    public event Action? QueueReplaced;

    /// <summary><see cref="Status"/>, <see cref="StatusDetail"/>, <see cref="Where"/> or <see cref="WhereDisplay"/> changed.</summary>
    public event Action? StatusChanged;

    /// <summary>What to do once the queue has finished; for this batch, and never saved.</summary>
    public WhenDone WhenDone
    {
        get => _whenDone;
        set
        {
            _whenDone = value;
            WhenDoneChanged?.Invoke();
        }
    }

    public event Action? WhenDoneChanged;

    /// <summary>
    /// Start the queue for the mode in the settings. It has said where downloads happen by the time this returns; the task
    /// ends once the local queue has been read, off the UI thread, or at once for a server, which connects in the background.
    /// </summary>
    public async Task StartAsync()
    {
        app.SettingsChanged += OnSettingsChanged;
        _offlineTimer.Interval = OfflineAfter;
        _offlineTimer.IsRepeating = false;
        _offlineTimer.Tick += (_, _) =>
        {
            if (Status == ModeStatus.Reconnecting && _session is { } session && !session.Queue.IsAttached)
                Use(session.Queue, ModeStatus.Offline, _reconnectProblem ?? $"{session.Display} · reconnecting");
        };
        _saveTimer.Interval = SaveAfter;
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();
        _scheduleTimer.Interval = ScheduleCheck;
        _scheduleTimer.Tick += (_, _) => ApplySchedule();
        _scheduleTimer.Start();
        await _switching.WaitAsync();
        try
        {
            await BeginAsync();
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>The mode changed (or a server was set up): close the current queue and start the right one.</summary>
    public async Task SwitchAsync()
    {
        await _switching.WaitAsync(); // one at a time: two at once would start two connections
        try
        {
            await CloseAsync();
            await BeginAsync();
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>Try reaching the server now instead of waiting for the next retry; connected, check it again.</summary>
    public async Task RetryNowAsync()
    {
        if (app.Settings.Mode != DownloadMode.Server || _switching.CurrentCount == 0)
            return;
        if (Status == ModeStatus.Connected && _session is { } session && session.Queue.IsAttached)
        {
            // connected: asking it something is the test, and running transfers carry on
            if (await FreeSpaceAsync(session) is { } free && ReferenceEquals(_session, session))
            {
                ServerFreeBytes = free;
                Use(session.Queue, ModeStatus.Connected, Connected(session));
                return;
            }
        }
        await SwitchAsync();
    }

    /// <summary>
    /// What a link leads to, read where the download will happen: on the server in server mode, so the site hears from
    /// the server alone; else here.
    /// </summary>
    /// <exception cref="Core.StageException">it couldn't be read, or the server can't be reached</exception>
    /// <exception cref="InvalidOperationException">before <see cref="StartAsync"/></exception>
    public async Task<(string Name, long? Size)> ReadLinkAsync(FileLink link, RequestDetails details, CancellationToken cancel)
    {
        if (Queue is RemoteQueue remote)
            return await remote.ReadAsync(link, details, cancel);
        var sites = Sites ?? throw new InvalidOperationException("The queue hasn't started.");
        var file = await sites.ResolveAsync(link, details, cancel);
        return (file.Name, file.Size);
    }

    /// <summary>Active downloads on this PC, which closing the app would interrupt (server mode carries on without it).</summary>
    public int ActiveHere => Queue is null ? 0 : Queue.Items.Count(i => _local is not null
        ? i.IsActive || i.State == QueueState.Waiting
        : i.State == QueueState.Transferring);

    /// <summary>Pause what runs here and save, for closing or updating.</summary>
    /// <param name="disconnect">whether the server's session ends too, as this PC won't be there to use it</param>
    public async Task PauseAndSaveAsync(bool disconnect = true)
    {
        if (_local is not null)
        {
            await _local.PauseAllAsync();
            Save();
        }
        else if (_session is not null)
        {
            Save();
            if (disconnect)
                await DisconnectAsync(); // stops transfers; the server carries on
        }
    }

    /// <summary>Close the session, saying so meanwhile, and never for longer than <see cref="DisconnectWait"/>.</summary>
    private async Task DisconnectAsync()
    {
        if (_session is not { } session)
            return;
        _offlineTimer.Stop();
        OnUi(() => Use(session.Queue, ModeStatus.Disconnecting, $"Disconnecting from {session.Display}"));
        _session = null;
        try
        {
            await session.DisposeAsync().AsTask().WaitAsync(DisconnectWait);
        }
        catch (TimeoutException)
        {
            // it finishes in the background; at exit the ssh processes end with the app
        }
    }

    public async ValueTask DisposeAsync()
    {
        app.SettingsChanged -= OnSettingsChanged;
        _scheduleTimer.Stop();
        await CloseAsync();
    }

    private async Task BeginAsync()
    {
        _stop = new CancellationTokenSource();
        var settings = app.Settings;
        _http = new HttpFetcher(settings.ConnectTimeout);
        Refresh(settings, _http);
        if (settings.Mode == DownloadMode.Server)
        {
            Place(DownloadPlace.Server, ServerDisplay ?? SomeServer);
            _ = ConnectAsync(_stop.Token);
            return;
        }
        if (WantsRemote(settings))
        {
            BeginRemote(settings, _http);
            return;
        }
        Place(DownloadPlace.ThisPc, ThisPc);
        _overall.BytesPerSecond = settings.SpeedLimit;
        _held = false;
        // not _local until it has been read: nothing may save it, empty, over the file meanwhile
        var local = new DownloadQueue(Downloader(settings, Sites!), Sites!, settings.DownloadFolder, settings.FilesAtOnce, CrashLog.Write);
        _queueUnread = !await ReadLocalQueueAsync(local);
        _local = local;
        local.Changed += OnLocalChanged;
        Use(local, ModeStatus.Local, settings.DownloadFolder);
        ApplySchedule();
    }

    /// <summary>
    /// Which links can be queued and how links to many files are read, for the mode: in server mode the server reads them
    /// (once it is connected), after what must read here, and what only this app has takes no links.
    /// </summary>
    private void Refresh(FerrySettings settings, HttpFetcher http)
    {
        var server = settings.Mode == DownloadMode.Server;
        Expander = server && _session is { } session
            ? session.Expander(http)
            : OfficialSites.Expander(http, settings, app.Secrets, downloadsElsewhere: server);
        Sites = OfficialSites.Catalog(http, settings, app.Secrets);
    }

    /// <summary>Note where downloads happen, and say so if that changed.</summary>
    private void Place(DownloadPlace where, string display)
    {
        if (where == Where && display == WhereDisplay)
            return;
        (Where, WhereDisplay) = (where, display);
        StatusChanged?.Invoke();
    }

    /// <summary>While Remote Queues is on, and not downloading on a server, the machine it names has the queue.</summary>
    private static bool WantsRemote(FerrySettings settings) => settings.Mode != DownloadMode.Server && RemoteQueuesPlugin.Plugin.IsOn(settings);

    private void BeginRemote(FerrySettings settings, HttpFetcher http)
    {
        var queue = RemoteQueuesPlugin.Queue(http, settings, app.Secrets, () => Sites);
        queue.Changed += e => OnUi(() => Changed?.Invoke(e));
        var detail = $"{queue.Name} at {queue.Address.Host}";
        queue.ProblemChanged += problem => OnUi(() =>
        {
            if (ReferenceEquals(_remote, queue))
                Use(queue, ModeStatus.Local, problem is null ? detail : $"{detail}: {problem}");
        });
        _remote = queue;
        _remoteWhich = RemoteQueuesPlugin.WhichOf(settings);
        Place(DownloadPlace.RemoteQueue, detail);
        queue.Start();
        Use(queue, ModeStatus.Local, detail);
    }

    private SegmentedDownload Downloader(FerrySettings settings, SiteCatalog sites) =>
        new(_http!, sites, OfficialSites.Download(settings), _overall, Hosts(settings));

    /// <summary>
    /// The cap on connections to one server. A changed setting makes a new one, for downloads that start from now on: those
    /// running keep the turns they hold, which they give back to the one they came from.
    /// </summary>
    private HostConnections Hosts(FerrySettings settings)
    {
        if (_hosts is null || _hostsAllow != settings.ConnectionsPerHost)
            (_hosts, _hostsAllow) = (new HostConnections(settings.ConnectionsPerHost), settings.ConnectionsPerHost);
        return _hosts;
    }

    /// <summary>
    /// Hold or let go of what runs on this PC, as the schedule says for this minute: the downloads here, or in server
    /// mode the transfers from the server, which keeps downloading in its own hours.
    /// </summary>
    private void ApplySchedule()
    {
        var held = app.Settings.Schedule is { } schedule && !schedule.IsOpen(TimeOnly.FromDateTime(DateTime.Now));
        if (held == _held)
            return;
        if (_local is { } local)
            _ = local.SetHeldAsync(held);
        else if (_session is { } session)
            _ = session.Queue.SetHeldAsync(held);
        else
            return;
        _held = held;
    }

    private async Task ConnectAsync(CancellationToken stop)
    {
        if (!ServerProfile.From(app.Settings).IsSet)
        {
            Use(null, ModeStatus.NotSetUp, "Set up a server in Plugins");
            return;
        }
        for (var attempt = 0; !stop.IsCancellationRequested; attempt++)
        {
            // after a failed try the line keeps saying why, with when the next try is, instead of flickering back
            if (attempt == 0)
                Use(null, ModeStatus.Connecting, $"Connecting to {ServerDisplay}");
            try
            {
                var session = await Task.Run(() => ServerSession.OpenAsync(app.Settings, app.Secrets, stop, _requests.Load(), CrashLog.Write, plugins: app.Installed.Store), stop);
                if (stop.IsCancellationRequested)
                {
                    await session.DisposeAsync();
                    return;
                }
                _session = session;
                // a session that was replaced (closed, or the mode changed) has nothing more to say
                bool Current() => ReferenceEquals(_session, session);
                session.Queue.Changed += e => OnUi(() =>
                {
                    if (!Current())
                        return;
                    if (e is not QueueItemUpdated)
                        SaveSoon();
                    Changed?.Invoke(e);
                });
                session.Queue.ConnectionChanged += attached => OnUi(() =>
                {
                    if (!Current())
                        return;
                    if (attached)
                    {
                        _offlineTimer.Stop();
                        _reconnectProblem = null;
                        Use(session.Queue, ModeStatus.Connected, Connected(session));
                    }
                    else if (Status != ModeStatus.Offline)
                    {
                        _reconnectProblem = null;
                        Use(session.Queue, ModeStatus.Reconnecting, $"Reconnecting to {session.Display}");
                        _offlineTimer.Stop();
                        _offlineTimer.Start();
                    }
                });
                session.ReconnectFailed += e => OnUi(() =>
                {
                    if (!Current())
                        return;
                    if (e is HostKeyChangedException or AgentVersionException || Status != ModeStatus.Reconnecting)
                    {
                        _offlineTimer.Stop();
                        Use(session.Queue, ModeStatus.Offline, Final(e)); // a changed key or another agent never waits
                    }
                    else
                    {
                        _reconnectProblem = e.Message; // shown if it is still down when the grace ends
                    }
                });
                ServerFreeBytes = await FreeSpaceAsync(session);
                OnUi(() =>
                {
                    if (!Current())
                        return;
                    if (_http is { } http)
                        Refresh(app.Settings, http); // now links to many files can be read on the server
                    Place(DownloadPlace.Server, session.Display);
                    ApplySchedule();
                    if (session.Queue.IsAttached)
                    {
                        Use(session.Queue, ModeStatus.Connected, Connected(session));
                    }
                    else
                    {
                        Use(session.Queue, ModeStatus.Reconnecting, $"Reconnecting to {session.Display}");
                        _offlineTimer.Stop();
                        _offlineTimer.Start();
                    }
                });
                return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is HostKeyChangedException or AgentVersionException)
            {
                if (!stop.IsCancellationRequested)
                    OnUi(() => Use(null, ModeStatus.Offline, Final(e)));
                return; // never retried without the user
            }
#pragma warning disable CA1031 // whatever stopped this try (a full disk for the agent cache, say), the next one may work
            catch (Exception e)
#pragma warning restore CA1031
            {
                if (stop.IsCancellationRequested)
                    return; // switched away meanwhile: what replaced this stays
                var wait = RetryAfter[Math.Min(attempt, RetryAfter.Length - 1)];
                OnUi(() => Use(null, ModeStatus.Offline, $"{e.Message} Trying again in {wait.TotalSeconds:0} s."));
                try
                {
                    await Task.Delay(wait, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>What to say of a failure only the user can put right: a changed host key, or an agent that couldn't be replaced.</summary>
    private static string Final(Exception e) => e is AgentVersionException mismatch ? ServerSetup.AgentNotReplaced(mismatch) : e.Message;

    /// <summary>Free space on the server, for the sidebar's card; null if it can't be had right now.</summary>
    private static async Task<long?> FreeSpaceAsync(ServerSession session)
    {
        try
        {
            return await session.Queue.FreeSpaceAsync();
        }
#pragma warning disable CA1031 // only for the card: unknown is fine, and the connection must not be retried for it
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private string Connected(ServerSession session) =>
        $"{session.Display}{(ServerFreeBytes is { } free ? $" · {Core.Text.Units.Size(free)} free" : "")}";

    private void Use(IDownloadQueue? queue, ModeStatus status, string detail)
    {
        var replaced = !ReferenceEquals(queue, Queue);
        (Queue, Status, StatusDetail) = (queue, status, detail);
        if (replaced)
            QueueReplaced?.Invoke();
        StatusChanged?.Invoke();
    }

    private void OnLocalChanged(QueueEvent e)
    {
        OnUi(() =>
        {
            if (e is not QueueItemUpdated)
                SaveSoon();
            Changed?.Invoke(e);
        });
    }

    /// <summary>Save once the burst of changes is over, not once per change.</summary>
    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>What was queued; interrupted downloads come back paused. A file that is only held for a moment (an antivirus, a sync tool) is
    /// given a moment. If it stays that way nothing is saved over it this time, as an empty queue would replace what is in it.</summary>
    /// <returns>whether it could be read</returns>
    private async Task<bool> ReadLocalQueueAsync(DownloadQueue local)
    {
        for (var tries = 3; ; tries--)
        {
            try
            {
                await Task.Run(() => local.Restore(_store.Load(), _requests.Load()));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (tries <= 1)
                {
                    CrashLog.Write("reading the queue", e);
                    return false;
                }
                CrashLog.Write("reading the queue, trying again", e);
                await Task.Delay(QueueReadWait);
            }
        }
    }

    /// <summary>
    /// What a restart needs: the local queue and its files' request details, or in server mode the request details alone
    /// (the server keeps the queue, but holds the details in memory only, so the PC gives them back after it restarts).
    /// </summary>
    private void Save()
    {
        try
        {
            if (_local is { } local && !_queueUnread)
            {
                _store.Save(local.Items);
                _requests.Save(local.RequestsToKeep());
            }
            else if (_session is { } session)
            {
                _requests.Save(session.Queue.RequestsToKeep());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // held for a moment (an antivirus, a sync tool): the next change saves again
        }
    }

    private void OnSettingsChanged()
    {
        var settings = app.Settings;
        var wantRemote = WantsRemote(settings);
        var wantLocal = settings.Mode != DownloadMode.Server && !wantRemote;
        if (wantLocal != (_local is not null) || wantRemote != (_remote is not null) || (wantRemote && _remoteWhich != RemoteQueuesPlugin.WhichOf(settings)))
        {
            _ = SwitchAsync();
            return;
        }
        if (_http is null)
            return;
        Refresh(settings, _http);
        if (_session is { } session)
        {
            // the transfers follow the settings at once, and the agent is told: its downloads follow them too
            _ = ReconfigureServerAsync(session, settings);
        }
        else if (_local is { } local && Sites is { } sites)
        {
            // speed limit and files at once apply at once; the rest to downloads that start from now on
            _overall.BytesPerSecond = settings.SpeedLimit;
            local.Reconfigure(Downloader(settings, sites), sites, settings.DownloadFolder, settings.FilesAtOnce);
            // a plugin turned off: its files wait, paused, for it to be turned back on
            foreach (var item in local.Items.Where(i => sites.IsOff(i.Link.SiteId) && (i.IsActive || i.State == QueueState.Waiting)))
                _ = local.PauseAsync(item.Id);
        }
        ApplySchedule();
    }

    /// <summary>The settings go to the server; what it can't use there is said on the status line.</summary>
    private async Task ReconfigureServerAsync(ServerSession session, FerrySettings settings)
    {
        try
        {
            session.RefreshPluginSecrets(); // one saved or removed since comes with the settings
            await session.Queue.ReconfigureAsync(settings);
        }
        catch (InvalidOperationException e)
        {
            OnUi(() =>
            {
                if (ReferenceEquals(_session, session))
                    Use(session.Queue, Status, e.Message);
            });
        }
    }

    private async Task CloseAsync()
    {
        await _stop.CancelAsync();
        if (_local is not null)
        {
            _local.Changed -= OnLocalChanged;
            await _local.PauseAllAsync();
            Save();
            _local = null;
        }
        if (_remote is { } remote)
        {
            _remote = null;
            await remote.DisposeAsync();
        }
        if (_session is not null)
            Save();
        await DisconnectAsync();
        _http?.Dispose();
        _http = null;
        _held = false;
        ServerFreeBytes = null;
        Use(null, ModeStatus.Local, "");
    }

    private void OnUi(Action action)
    {
        if (ui.HasThreadAccess)
            action();
        else
            ui.TryEnqueue(() => action());
    }
}
