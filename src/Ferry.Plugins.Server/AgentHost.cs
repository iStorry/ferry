using Ferry.Core.Sites;
using Ferry.Core.Text;
using Ferry.Core;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Ferry.Plugins.Official;
using Ferry.Core.Browse;
using Ferry.Core.Download;
using Ferry.Core.Http;
using Ferry.Core.Queue;
using Ferry.Core.Settings;

namespace Ferry.Plugins.Server;

/// <summary>Where the agent keeps its files on the server, all under one folder (by default ~/.ferry).</summary>
public sealed record AgentPaths(string Root)
{
    /// <summary>The agent's folder under the user's home, by default.</summary>
    public const string FolderName = ".ferry";

    /// <summary>Where the agent builds are, under <see cref="FolderName"/>; "current" points at the one installed last.</summary>
    public const string BinFolderName = "bin";

    /// <summary>The default folder as a shell on the server sees it, which expands $HOME.</summary>
    public const string ShellRoot = "$HOME/" + FolderName;

    /// <summary>The agent installed last, quoted for a shell on the server: what the attach and the reads run.</summary>
    public const string ShellCurrentAgent = "\"" + ShellRoot + "/" + BinFolderName + "/current\"";

    /// <summary>The agent's log as the user finds it on the server.</summary>
    public const string LogOnServer = "~/" + FolderName + "/agent.log";

    public static AgentPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FolderName));

    public string QueueFile => Path.Combine(Root, "queue.json");

    /// <summary>The albums the agent watches for new files.</summary>
    public string WatchedFile => Path.Combine(Root, "watched.json");

    /// <summary>The packages of the plugins installed here, each "{id}.ferryplugin", put there by the PC.</summary>
    public string PluginsFolder => Path.Combine(Root, "plugins");

    /// <summary>The settings the PC last sent, so the agent can carry on after a restart before the PC is back.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string RunFolder => Path.Combine(Root, "run");

    public string SocketPath => Path.Combine(RunFolder, "agent.sock");

    /// <summary>The running agent's process id, written by "ferry agent run".</summary>
    public string PidFile => Path.Combine(RunFolder, "agent.pid");

    /// <summary>Held (locked) by the running agent for as long as it runs: there is only ever one.</summary>
    public string LockFile => Path.Combine(RunFolder, "agent.lock");

    /// <summary>What the agent did and what went wrong: its output when it runs in the background.</summary>
    public string LogFile => Path.Combine(Root, "agent.log");

    /// <summary>"~/x" → the user's home folder followed by x.</summary>
    public static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
            : path;
}

/// <summary>
/// The server side of server mode: runs a <see cref="DownloadQueue"/> and answers the PC's requests
/// (<see cref="AgentCommand"/>) over any number of connections, on a Unix socket or any pair of streams. Downloads
/// don't depend on a PC being connected. The queue is kept in <see cref="AgentPaths.QueueFile"/>, and files that were
/// downloading when the agent stopped carry on when it starts again.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private static readonly Dictionary<string, string> NoEnvironment = [];

    // what changes the queue runs one at a time per PC, in the order that PC asked; reads run alongside
    private static readonly HashSet<AgentCommand> Mutating =
    [
        AgentCommand.Settings, AgentCommand.Add, AgentCommand.Details, AgentCommand.Pause, AgentCommand.Resume, AgentCommand.Cancel,
        AgentCommand.Retry, AgentCommand.Remove, AgentCommand.Rename, AgentCommand.SetLimit, AgentCommand.PauseAll, AgentCommand.ResumeAll,
        AgentCommand.Collected,
    ];
    private readonly AgentPaths _paths;
    private readonly string _version;
    private readonly IReadOnlyList<AgentPlugin> _plugins;
    private readonly AgentBrowsing? _browsing;
    private readonly AgentLookup? _lookup;
    private readonly AlbumWatcher _watcher;
    private readonly HttpFetcher _http;
    private readonly SpeedLimit _overall = new();
    private readonly QueueStore _store;
    private readonly Lock _saveLock = new();
    private readonly Lock _sessionsLock = new();
    private readonly List<Session> _sessions = [];
    private readonly Dictionary<string, (long Length, DateTime Written, string Sha256)> _hashes = [];

    // a save writes and syncs the whole queue: one soon after a change covers every change until then
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SaveRetryWait = TimeSpan.FromSeconds(5);

    private const long LogMaxBytes = 2 << 20;
    private const int LogCheckEvery = 200;
    private static string? s_logFile; // the agent's log, when it runs detached (its stderr goes there)
    private static int s_logLines;
    private DownloadQueue _queue = null!;
    private SiteCatalog _sites = null!;
    private LinkExpander _expander = new([]);
    private string _folder = "";
    private HostConnections _hosts;
    private FerrySettings _settings;
    private Dictionary<string, string> _sent = []; // the plugins' secrets as the PC last sent them; never written down
    private Ferry.Core.Plugins.ISiteBrowsing? _site; // what reads the browsed site now; null while its plugin is off, or there is none
    private Ferry.Core.Plugins.ITitleLookup? _titles; // what searches a site for titles now; null likewise
    private volatile bool _saving = true;
    private volatile bool _unsaved;
    private int _saveScheduled;

    private AgentHost(AgentPaths paths, string version, FerrySettings settings, IReadOnlyList<AgentPlugin> plugins, AgentBrowsing? browsing, AgentLookup? lookup)
    {
        (_paths, _version, _plugins, _browsing, _lookup, _settings) = (paths, version, plugins, browsing, lookup, settings);
        // the watcher reads through whatever reads the site now, and holds off while nothing does
        _watcher = new AlbumWatcher(new SitePages(this), paths.WatchedFile, IAlbumWatcher.Every, TimeProvider.System, mayRead: () => _site is not null,
                                    problem: (what, e) => Log($"{what}: {e.Message}"));
        _http = new HttpFetcher(settings.ConnectTimeout);
        _hosts = new HostConnections(settings.ConnectionsPerHost);
        _store = new QueueStore(paths.QueueFile);
    }

    /// <summary>The queue the agent runs; for tests.</summary>
    internal DownloadQueue Queue => _queue;

    /// <summary>Start the agent: the last settings the PC sent, and the queue as it was.</summary>
    /// <param name="version">the version to report; tests change it</param>
    /// <param name="plugins">the installed plugins, loaded or not, already added to what the agent downloads with</param>
    /// <param name="browsing">the plugin whose site is browsed, when one of them has a site to browse</param>
    /// <param name="lookup">the plugin whose site is searched for titles, when one of them does that</param>
    public static AgentHost Start(AgentPaths paths, string? version = null, IReadOnlyList<AgentPlugin>? plugins = null, AgentBrowsing? browsing = null,
                                  AgentLookup? lookup = null)
    {
        CreatePrivateFolder(paths.Root);
        var settings = ReadSettings(paths);
        var host = new AgentHost(paths, version ?? Product.Build, Build(paths, settings), plugins ?? [], browsing, lookup);
        host.Apply(settings);
        s_logFile = paths.LogFile;
        // the request details are kept in memory only: a PC that connects sends them again (AgentCommand.Details)
        host._queue.Restore(host._store.Load(), new Dictionary<Guid, RequestDetails>(), resumeInterrupted: true);
        host._queue.Changed += host.OnQueueChanged; // after the restore: what was in the queue before isn't news, and is saved already
        host._watcher.Changed += host.OnWatchedChanged;
        host._watcher.Start();
        return host;
    }

    /// <summary>
    /// The agent's lock, or null if another agent holds it. Whoever holds it is the one agent on these paths, so two
    /// PCs (or the app and the CLI) starting one at the same moment can't end up with two on one queue.
    /// </summary>
    public static FileStream? TryLock(AgentPaths paths)
    {
        CreatePrivateFolder(paths.Root);
        CreatePrivateFolder(paths.RunFolder);
        try
        {
            return new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) when (File.Exists(paths.LockFile))
        {
            return null; // locked: an agent runs
        }
    }

    /// <summary>Accept connections on <see cref="AgentPaths.SocketPath"/> (owner only) until cancelled.</summary>
    public async Task ListenAsync(CancellationToken cancel)
    {
        CreatePrivateFolder(_paths.RunFolder);
        File.Delete(_paths.SocketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_paths.SocketPath));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_paths.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen();
        try
        {
            while (true)
            {
                var socket = await listener.AcceptAsync(cancel);
                _ = ServeSocketAsync(socket, cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // stopping
        }
        finally
        {
            File.Delete(_paths.SocketPath);
        }
    }

    /// <summary>Answer one PC's requests until it disconnects; its downloads carry on.</summary>
    public async Task ServeAsync(Stream input, Stream output, CancellationToken cancel)
    {
        await using var lines = new AgentLines(input, output);
        var session = new Session(lines);
        var writing = session.WriteAllAsync(cancel);
        try
        {
            while (await lines.ReadAsync(cancel) is { } line)
            {
                var request = Parse(line);
                if (request is null)
                    session.Send(new AgentMessage(Answers: 0, Error: "Not a request Ferry's agent understands."));
                else if (request.Command == AgentCommand.Hello)
                    Hello(session, request);
                else if (!session.Greeted)
                    session.Send(new AgentMessage(Answers: request.Id, Error: "Say hello first."));
                else
                    session.Track(AnswerAsync(session, request));
            }
        }
        catch (IOException)
        {
            // the PC went away
        }
        finally
        {
            bool greeted;
            lock (_sessionsLock)
                greeted = _sessions.Remove(session);
            if (greeted)
                Log("PC disconnected");
            await session.CloseAsync(); // what it was still reading for it stops: nobody is there for the answer
            await writing;
        }
    }

    /// <summary>Stop: the queue is saved as it is, so what was downloading carries on at the next start.</summary>
    public async ValueTask DisposeAsync()
    {
        _saving = false;
        Save(force: true);
        await _queue.PauseAllAsync();
        await _watcher.DisposeAsync();
        _http.Dispose();
    }

    private async Task ServeSocketAsync(Socket socket, CancellationToken cancel)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            await ServeAsync(stream, stream, cancel);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // one connection's end never stops the agent
        }
    }

    private static AgentRequest? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, AgentJson.Default.AgentRequest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Hello(Session session, AgentRequest request)
    {
        if (session.Greeted)
        {
            session.Send(new AgentMessage(Answers: request.Id, Version: _version, ErrorStage: Stage.Server, Error: "Hello was said already."));
            return;
        }
        if (request.Version != _version)
        {
            session.Send(new AgentMessage(Answers: request.Id, Version: _version, ErrorStage: Stage.Server,
                                          Error: $"This agent is version {_version}, the PC is {request.Version}."));
            return;
        }
        try
        {
            UseSettings(request.Settings ?? [], request.Secrets);
        }
        catch (InvalidOperationException e)
        {
            session.Send(new AgentMessage(Answers: request.Id, Version: _version, ErrorStage: Stage.Server, Error: e.Message));
            return;
        }
        lock (_sessionsLock)
        {
            // the answer goes first, then every event after the snapshot in it
            session.Greeted = true;
            _sessions.Add(session);
            Log($"PC connected ({_sessions.Count} now)");
            session.Send(new AgentMessage(Answers: request.Id, Version: _version, Items: [.. _queue.Items.Select(RemoteItem.From)], Plugins: [.. _plugins],
                                          Albums: [.. _watcher.Albums]));
        }
    }

    private async Task AnswerAsync(Session session, AgentRequest request)
    {
        var closing = session.Closing;
        var mutating = Mutating.Contains(request.Command);
        AgentMessage answer;
        try
        {
            if (mutating)
                await session.OneChangeAtATime.WaitAsync(closing);
            try
            {
                // in line as asked, and now off the read loop: a big change mustn't keep the next line, a heartbeat say, from being read
                await Task.Yield();
                answer = await HandleAsync(request, closing) with { Answers = request.Id };
            }
            finally
            {
                if (mutating)
                    session.OneChangeAtATime.Release();
            }
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            return; // the PC is gone: nobody is there for the answer
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            answer = new AgentMessage(Answers: request.Id, Error: e.Message);
        }
        catch (StageException e)
        {
            answer = new AgentMessage(Answers: request.Id, Error: e.Message, ErrorStage: e.Stage);
        }
#pragma warning disable CA1031 // the PC waits for an answer: it gets one, with what went wrong, and the agent goes on
        catch (Exception e)
#pragma warning restore CA1031
        {
            Log($"{request.Command} failed: {e}");
            answer = new AgentMessage(Answers: request.Id, Error: $"The agent couldn't do that: {e.Message}");
        }
        session.Send(answer);
    }

    /// <summary>A line in the agent's log (its standard error, which goes to <see cref="AgentPaths.LogFile"/>).</summary>
    public static void Log(string text)
    {
        try
        {
            Console.Error.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {text}");
            if (s_logFile is { } file && Interlocked.Increment(ref s_logLines) % LogCheckEvery == 0)
                TrimLog(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // nowhere to write: nothing to do about it
        }
    }

    /// <summary>
    /// Past <see cref="LogMaxBytes"/>, the log moves to agent.log.old and starts again. The agent's stderr was opened
    /// to append (">>"), so emptying the file in place is enough: the next line goes to its new end.
    /// </summary>
    private static void TrimLog(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length < LogMaxBytes)
            return;
        File.Copy(file, file + ".old", overwrite: true);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.SetLength(0);
    }

    /// <param name="cancel">the PC's session is over: nobody is there for the answer</param>
    private async Task<AgentMessage> HandleAsync(AgentRequest request, CancellationToken cancel)
    {
        var ok = new AgentMessage();
        switch (request.Command)
        {
            case AgentCommand.Ping:
                return ok;
            case AgentCommand.Settings:
                UseSettings(request.Settings ?? [], request.Secrets);
                return ok;
            case AgentCommand.Add:
                var added = await _queue.AddAsync(request.Lines ?? [], new AddOptions(request.Name, Details: request.Details, Paused: request.Flag ?? false,
                                                                                      Sha256: request.Sha256, Album: request.Folder));
                return new AgentMessage(Items: [.. added.Added.Select(RemoteItem.From)],
                                        Duplicates: [.. added.Duplicates.Select(RemoteItem.From)], Rejected: [.. added.Rejected]);
            case AgentCommand.Details:
                await _queue.SetDetailsAsync(Item(request).Id, request.Details is { IsEmpty: false } details
                    ? details
                    : throw new InvalidOperationException("No request details were sent."));
                return ok;
            case AgentCommand.Expand:
                return new AgentMessage(Files: await _expander.TryListAsync(Link(request), request.Details ?? RequestDetails.None, cancel));
            case AgentCommand.Read:
                var read = await ReadAsync(Link(request), request.Details ?? RequestDetails.None, cancel);
                return new AgentMessage(Name: read.Name, Size: read.Size);
            case AgentCommand.Pause:
                await _queue.PauseAsync(Item(request).Id);
                return ok;
            case AgentCommand.Resume:
                await _queue.ResumeAsync(Item(request).Id);
                return ok;
            case AgentCommand.Cancel:
                await _queue.CancelAsync(Item(request).Id);
                return ok;
            case AgentCommand.Retry:
                await _queue.RetryAsync(Item(request).Id);
                return ok;
            case AgentCommand.Remove:
                await _queue.RemoveAsync(Item(request).Id, request.Flag ?? false);
                return ok;
            case AgentCommand.Rename:
                await _queue.RenameAsync(Item(request).Id, request.Name);
                return ok;
            case AgentCommand.SetLimit:
                await _queue.SetSpeedLimitAsync(Item(request).Id, request.BytesPerSecond ?? 0);
                return ok;
            case AgentCommand.PauseAll:
                await _queue.PauseAllAsync();
                return ok;
            case AgentCommand.ResumeAll:
                await _queue.ResumeAllAsync();
                return ok;
            case AgentCommand.FileInfo:
                var finished = Finished(request);
                var (path, size, sha256) = await FileInfoAsync(finished, cancel);
                return new AgentMessage(Path: path, Size: size, Sha256: sha256, Folder: OwnFolder(finished));
            case AgentCommand.Collected:
                await CollectedAsync(Item(request), keepFile: request.Flag ?? false);
                return ok;
            case AgentCommand.Space:
                return new AgentMessage(FreeBytes: FreeBytes());
            case AgentCommand.Stats:
                return new AgentMessage(Stats: Stats());
            case AgentCommand.Log:
                return new AgentMessage(Log: await AgentStats.TailAsync(_paths.LogFile, AgentStats.LogTailBytes));
            case AgentCommand.Browse:
                return new AgentMessage(Page: await Site().OpenAsync(Page(request.Link), cancel));
            case AgentCommand.Watch:
                await _watcher.WatchAsync(Album(request));
                return ok;
            case AgentCommand.Unwatch:
                await _watcher.UnwatchAsync(Page(request.Link));
                return ok;
            case AgentCommand.MarkSeen:
                await _watcher.MarkSeenAsync(Album(request));
                return ok;
            case AgentCommand.CheckWatched:
                Site();
                await _watcher.CheckNowAsync(cancel);
                return ok;
            case AgentCommand.Albums:
                return new AgentMessage(Cards: [.. await Site().AlbumsAsync(request.Query ?? AlbumQuery.Latest, cancel)]);
            case AgentCommand.Thumbnail:
                return Uri.TryCreate(request.Link, UriKind.Absolute, out var picture)
                    ? new AgentMessage(Picture: await Site().ThumbnailAsync(picture, cancel))
                    : throw new InvalidOperationException("That is not a picture's address.");
            case AgentCommand.AlbumDetails:
                return new AgentMessage(Details: await Site().DetailsAsync(Page(request.Link), cancel));
            case AgentCommand.TitleSearch:
                var titles = _titles ?? throw new InvalidOperationException(_lookup is { } lookup
                    ? $"{lookup.Name} is off, so nothing is looked up."
                    : "No plugin on the server looks titles up.");
                return new AgentMessage(Titles: [.. await titles.SearchAsync(request.Name ?? "", cancel)]);
            default:
                throw new InvalidOperationException($"The agent doesn't know \"{request.Command}\".");
        }
    }

    private QueueItem Item(AgentRequest request) =>
        request.Item is { } id && _queue.Find(id) is { } item
            ? item
            : throw new InvalidOperationException("That file is not in the server's queue.");

    private static Uri Link(AgentRequest request) =>
        Uri.TryCreate(request.Link, UriKind.Absolute, out var link) ? link : throw new InvalidOperationException($"\"{request.Link}\" is not a link.");

    /// <summary>What a link to one file leads to, as its site says, before it is added.</summary>
    /// <exception cref="StageException">it isn't a link the server can download, or its site couldn't be asked</exception>
    private async Task<(string Name, long? Size)> ReadAsync(Uri link, RequestDetails details, CancellationToken cancel)
    {
        if (!_sites.TryParse(link.ToString(), out var file, out var reason))
            throw new StageException(Stage.Link, reason);
        var resolved = await _sites.ResolveAsync(file, details, cancel);
        return (resolved.Name, resolved.Size);
    }

    /// <summary>What reads the browsed site now.</summary>
    /// <exception cref="InvalidOperationException">no plugin here has a site to browse, or it is off</exception>
    private Ferry.Core.Plugins.ISiteBrowsing Site() =>
        _site ?? throw new InvalidOperationException(_browsing is { } browsing
            ? $"{browsing.Name} is off, so nothing is read from its site."
            : "No plugin on the server has a site to browse.");

    /// <exception cref="InvalidOperationException">not an album's or file's page on the browsed site</exception>
    private PageLink Page(string? link) =>
        _browsing?.TryReadPage(link ?? "", _settings) ?? throw new InvalidOperationException($"\"{link}\" is not an album or file link the server can browse.");

    private static BrowsedPage Album(AgentRequest request) =>
        request.Page is { Link.Kind: PageKind.Album } page ? page : throw new InvalidOperationException("Only albums can be watched.");

    /// <summary>Every connected PC hears of a change to the watched albums, with all of them.</summary>
    private void OnWatchedChanged()
    {
        var message = new AgentMessage(Event: AgentEventKind.WatchedChanged, Albums: [.. _watcher.Albums]);
        lock (_sessionsLock)
        {
            foreach (var session in _sessions)
                session.Send(message);
        }
    }

    /// <summary>The watcher's reader: pages as the site's plugin reads them now.</summary>
    private sealed class SitePages(AgentHost host) : IPageBrowser
    {
        public Task<BrowsedPage> OpenAsync(PageLink link, CancellationToken cancel) =>
            host._site is { } site ? site.OpenAsync(link, cancel) : throw new StageException(Stage.Resolve, "Nothing reads the site right now.");
    }

    private QueueItem Finished(AgentRequest request) =>
        Item(request) is { State: QueueState.Done, Path: not null } item
            ? item
            : throw new InvalidOperationException("That file has not finished downloading on the server.");

    /// <summary>The folder of its own under the download folder the item went into (an album's), or null.</summary>
    private string? OwnFolder(QueueItem item)
    {
        var relative = Path.GetRelativePath(_folder, item.Folder);
        return relative is "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
               || relative.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? null
            : relative;
    }

    /// <summary>
    /// The finished file's size and SHA-256: the hash the queue knows if the file is still the size it finished at, else
    /// computed once (a file from an older build, or one changed since).
    /// </summary>
    private async Task<(string Path, long Size, string Sha256)> FileInfoAsync(QueueItem item, CancellationToken cancel)
    {
        var path = item.Path!;
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new InvalidOperationException($"The finished file is no longer on the server ({path}).");
        if (item.Sha256 is { } fromQueue && (item.Total ?? info.Length) == info.Length)
            return (path, info.Length, fromQueue);
        lock (_hashes)
        {
            if (_hashes.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
                return (path, known.Length, known.Sha256);
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel));
        lock (_hashes)
            _hashes[path] = (info.Length, info.LastWriteTimeUtc, sha256);
        return (path, info.Length, sha256);
    }

    private async Task CollectedAsync(QueueItem item, bool keepFile)
    {
        if (item.State is not (QueueState.Done or QueueState.Skipped))
            throw new InvalidOperationException("That file has not finished downloading on the server.");
        await _queue.RemoveAsync(item.Id, deleteParts: false);
        if (_unsaved)
            Save(); // out of the saved queue before its file goes: a crash now can't leave a finished file that isn't there
        if (item.Path is not { } path)
            return;
        lock (_hashes)
            _hashes.Remove(path);
        if (!keepFile && item.State == QueueState.Done)
            File.Delete(path);
    }

    private long? FreeBytes()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            return new DriveInfo(_folder).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Use and keep <paramref name="settings"/> from the PC, so the agent starts with them next time too.</summary>
    /// <exception cref="InvalidOperationException">they can't be used on the server; the message says why</exception>
    /// <param name="secrets">the plugins' secrets, when the PC sent them along; null leaves those it sent before</param>
    private void UseSettings(Dictionary<string, string> settings, Dictionary<string, string>? secrets = null)
    {
        _sent = secrets ?? _sent;
        try
        {
            Apply(settings);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException($"The settings can't be used on the server: {e.Message}", e);
        }
        WriteSettings(settings);
    }

    /// <summary>Use <paramref name="settings"/> from the PC: new downloads follow them, running ones carry on.</summary>
    private void Apply(Dictionary<string, string> settings)
    {
        var resolved = Build(_paths, settings);
        _settings = resolved;
        var context = new PluginContext(_http, resolved, Secrets: null) { Sent = _sent };
        _site = _browsing is { } browsing && browsing.IsOn(resolved) ? browsing.Make(context) : null;
        _titles = _lookup is { } lookup && lookup.IsOn(resolved) ? lookup.Make(context) : null;
        // the server reads everything a PC in server mode doesn't: links to many files too
        _expander = OfficialSites.Expander(_http, resolved, secrets: null, downloadsElsewhere: false, _sent);
        var sites = OfficialSites.Catalog(_http, resolved, secrets: null, _sent);
        _sites = sites;
        // the server folder is only a staging area: the PC applies the rules for existing files and for sorting when it
        // brings them over, and checks each against the hash made here
        var options = resolved.Download with { OnExisting = ExistingFile.Rename, SortByType = false, AlwaysHash = true };
        _overall.BytesPerSecond = resolved.SpeedLimit;
        _hosts = _hosts.WithCap(resolved.ConnectionsPerHost);
        var downloader = new SegmentedDownload(_http, sites, options, _overall, _hosts);
        _folder = Path.GetFullPath(AgentPaths.ExpandHome(resolved.Text(ServerKeys.ServerFolder)));
        if (_queue is null)
            _queue = new DownloadQueue(downloader, sites, _folder, resolved.FilesAtOnce, (what, e) => Log($"{what}: {e}"));
        else
            _queue.Reconfigure(downloader, sites, _folder, resolved.FilesAtOnce);
    }

    /// <summary>The agent and its server as they are now, for the PC's Server page.</summary>
    private AgentStats Stats()
    {
        var items = _queue.Items;
        int sessions;
        lock (_sessionsLock)
            sessions = _sessions.Count;
        return AgentStats.Collect(_folder, sessions,
            downloading: items.Count(i => i.State is QueueState.Resolving or QueueState.Downloading),
            waiting: items.Count(i => i.State == QueueState.Waiting),
            finished: items.Count(i => i.State == QueueState.Done),
            bytesPerSecond: items.Where(i => i.State == QueueState.Downloading).Sum(i => double.IsFinite(i.BytesPerSecond) ? i.BytesPerSecond : 0));
    }

    private static FerrySettings Build(AgentPaths paths, Dictionary<string, string> settings) =>
        FerrySettings.Build(new AppPaths(paths.Root, paths.Root), settings, NoEnvironment);

    private void OnQueueChanged(QueueEvent e)
    {
        // `agent read` goes by the saved queue, and the PC may ask for a file as soon as it hears the file is done
        if (e is QueueItemStateChanged { Item.State: QueueState.Done })
            Save();
        else if (e is not QueueItemUpdated)
            SaveSoon(SaveDelay);
        switch (e)
        {
            case QueueItemAdded { Item: var added }:
                Log($"added: {added.Link.Original}");
                break;
            case QueueItemStateChanged { Item: { State: QueueState.Failed } failed }:
                Log($"failed: {failed.Link.Original} ({failed.FailedAt}): {failed.Error}");
                break;
            case QueueItemStateChanged { Item: { State: QueueState.Done } done }:
                Log($"done: {Path.GetFileName(done.Path)} ({Units.Size(done.Total ?? done.Done)})");
                break;
            case QueueItemStateChanged { Item: { State: QueueState.Paused or QueueState.Cancelled } stopped, Was: not QueueState.Paused }:
                Log($"{stopped.State.ToString().ToLowerInvariant()}: {stopped.Link.Original}");
                break;
            case QueueItemRemoved { Item: var removed }:
                Log(removed.State == QueueState.Done ? $"brought over: {Path.GetFileName(removed.Path)}" : $"removed: {removed.Link.Original}");
                break;
        }
        var message = new AgentMessage(Event: e switch
        {
            QueueItemAdded => AgentEventKind.Added,
            QueueItemStateChanged => AgentEventKind.StateChanged,
            QueueItemRemoved => AgentEventKind.Removed,
            _ => AgentEventKind.Updated,
        }, Item: RemoteItem.From(e.Item), Was: (e as QueueItemStateChanged)?.Was);
        lock (_sessionsLock)
        {
            foreach (var session in _sessions)
                session.Send(message, droppable: e is QueueItemUpdated);
        }
    }

    /// <summary>Save in a moment, along with whatever else changes until then.</summary>
    private void SaveSoon(TimeSpan delay)
    {
        _unsaved = true;
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 0)
            _ = SaveAfterAsync(delay);
    }

    private async Task SaveAfterAsync(TimeSpan delay)
    {
        await Task.Delay(delay);
        Volatile.Write(ref _saveScheduled, 0); // first: a change from here on schedules its own save
        if (_unsaved)
            Save();
    }

    /// <param name="force">save even after <see cref="DisposeAsync"/> stopped saving changes</param>
    private void Save(bool force = false)
    {
        if (!_saving && !force)
            return;
        lock (_saveLock)
        {
            _unsaved = false;
            try
            {
                _store.Save(_queue.Items);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log($"couldn't save the queue: {e.Message}");
                SaveSoon(SaveRetryWait);
            }
        }
    }

    private static Dictionary<string, string> ReadSettings(AgentPaths paths)
    {
        try
        {
            return File.Exists(paths.SettingsFile)
                ? JsonSerializer.Deserialize(File.ReadAllText(paths.SettingsFile), ServerFileJson.Default.Settings) ?? []
                : [];
        }
        catch (JsonException)
        {
            return []; // the PC sends them again when it connects
        }
    }

    private void WriteSettings(Dictionary<string, string> settings)
    {
        try
        {
            File.WriteAllText(_paths.SettingsFile + ".tmp", JsonSerializer.Serialize(settings, ServerFileJson.Default.Settings));
            File.Move(_paths.SettingsFile + ".tmp", _paths.SettingsFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // only needed after a restart, and the PC sends them again then
        }
    }

    private static void CreatePrivateFolder(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>One connected PC: its messages go out in the order they were sent.</summary>
    /// <remarks>Its token and gate end with <see cref="CloseAsync"/>, once what was answering has stopped.</remarks>
    [SuppressMessage("Design", "CA1001", Justification = "CloseAsync disposes them, after the answers that use them.")]
    private sealed class Session(AgentLines lines)
    {
        private readonly Channel<AgentMessage> _outbox = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions { SingleReader = true });
        private readonly List<Task> _answering = [];
        private readonly CancellationTokenSource _closing = new();

        // a PC that reads slowly (or a connection gone quiet that sshd hasn't noticed) mustn't fill the agent's memory:
        // past this, progress updates are dropped; the next one, or the next hello, says where things are
        private const int MaxQueued = 10_000;

        public bool Greeted { get; set; }

        /// <summary>Cancelled when the PC's connection ends: what was being read for it stops.</summary>
        public CancellationToken Closing => _closing.Token;

        /// <summary>Held by the request changing the queue, so this PC's changes happen in the order it asked.</summary>
        public SemaphoreSlim OneChangeAtATime { get; } = new(1, 1);

        private int _queued; // counted here: a single-reader channel can't count itself

        public void Send(AgentMessage message, bool droppable = false)
        {
            if (droppable && Volatile.Read(ref _queued) >= MaxQueued)
                return;
            if (_outbox.Writer.TryWrite(message))
                Interlocked.Increment(ref _queued);
        }

        public void Track(Task answering)
        {
            lock (_answering)
            {
                _answering.RemoveAll(t => t.IsCompleted);
                _answering.Add(answering);
            }
        }

        public async Task WriteAllAsync(CancellationToken cancel)
        {
            try
            {
                await foreach (var message in _outbox.Reader.ReadAllAsync(cancel))
                {
                    Interlocked.Decrement(ref _queued);
                    await lines.WriteAsync(message, AgentJson.Default.AgentMessage, cancel);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // the PC went away; what it missed, it gets in the next hello
            }
        }

        public async Task CloseAsync()
        {
            await _closing.CancelAsync();
            Task[] answering;
            lock (_answering)
                answering = [.. _answering];
            await Task.WhenAll(answering);
            _outbox.Writer.TryComplete();
            _closing.Dispose();
            OneChangeAtATime.Dispose();
        }
    }
}
