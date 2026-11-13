using System.Diagnostics.CodeAnalysis;
using Ferry.Core;
using Ferry.Core.Browse;
using Ferry.Plugins.Server;
using Microsoft.UI.Dispatching;

namespace Ferry.App.Services;

/// <summary>
/// The page reader and album watcher for the Browser page, as <see cref="QueueHost"/> keeps the queue. A site is browsed
/// by an installed plugin, which runs on the server: the agent reads its pages and its list of albums and watches albums,
/// over the queue's connection, and nothing is read while the server can't be reached. What was read is kept in memory
/// only.
/// </summary>
public sealed class BrowseHost : IAsyncDisposable
{
    private readonly AppServices _app;
    private readonly DispatcherQueue _ui;

    // pictures kept in memory; past this many they are dropped
    private const int MaxPictures = 1000;

    /// <summary>How long a page of the list, or an album, read is shown again as it was rather than read again.</summary>
    public static readonly TimeSpan ListKeep = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan PageKeep = TimeSpan.FromMinutes(10);

    private const int MaxListPages = 200;
    private const int MaxPages = 100;

    // pages of the list and albums read lately; used on the UI thread only
    private readonly Dictionary<AlbumQuery, (IReadOnlyList<AlbumCard> Albums, DateTimeOffset At)> _listPages = [];
    private readonly Dictionary<string, (Task<BrowsedPage> Read, DateTimeOffset At)> _pages = new(StringComparer.Ordinal);

    // the albums read so far, and their pictures; used on the UI thread only
    private List<AlbumCard>? _albums;
    private readonly Dictionary<Uri, byte[]> _pictures = [];

    // the albums' details read so far, by slug; used on the UI thread only
    private readonly Dictionary<string, AlbumDetails> _details = new(StringComparer.Ordinal);

    private readonly NoWatcher _noWatcher = new();
    private RemoteQueue? _remote;
    private RemotePageBrowser? _remoteBrowser;
    private RemoteAlbumWatcher? _remoteWatcher;

    public BrowseHost(AppServices app, DispatcherQueue ui) => (_app, _ui) = (app, ui);

    private bool ServerMode => _app.Settings.Mode == DownloadMode.Server;

    /// <summary>The installed plugin whose site is browsed: the first that has one and is on. Null when there is none.</summary>
    public InstalledPlugin? Plugin =>
        _app.Installed.All.FirstOrDefault(installed => installed.Stored.Manifest.Pages is not null && installed.Plugin.IsOn(_app.Settings));

    /// <summary>There is a site to browse, so the Browser has a place in the window. A decoy has none.</summary>
    public bool IsOffered => !_app.IsDecoy && Plugin is not null;

    /// <summary>What the browsed site's plugin is called.</summary>
    public string SiteName => Plugin?.Stored.Manifest.Name ?? "";

    /// <summary>Whether a file came from the browsed site, by its site's id.</summary>
    public bool IsOwn(string siteId) => Plugin?.Plugin.Owns(siteId) == true;

    /// <summary><paramref name="text"/> as an album's or a file's page on the browsed site; false, with the reason, when it isn't one.</summary>
    public bool TryReadPage(string text, [NotNullWhen(true)] out PageLink? page, [NotNullWhen(false)] out string? reason)
    {
        page = null;
        reason = null;
        if (Plugin?.Stored.Manifest is { Pages: { } pages } manifest && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var link)
            && manifest.Claims.Claims(link, _app.Settings) && pages.TryRead(link, out page))
            return true;
        reason = $"\"{text.Trim()}\" is not an album or file link{(SiteName.Length > 0 ? $" of {SiteName}" : "")}.";
        return false;
    }

    /// <summary>Reads pages and the list of albums now, or null while they can't be read (<see cref="Unavailable"/> says why).</summary>
    public RemotePageBrowser? Browser => _remote is { IsAttached: true } && ServerProblem is null ? _remoteBrowser : null;

    /// <summary>The server's watcher once the server has been reached; before that, one with nothing in it.</summary>
    public IAlbumWatcher Watcher => _remoteWatcher is { } remote ? remote : _noWatcher;

    /// <summary>Where pages are read, for the page to say.</summary>
    public string Where => $"Read by {_app.Queue.ServerDisplay ?? "your server"}";

    /// <summary>Why pages can't be read now, or null when they can.</summary>
    public string? Unavailable => Browser is not null ? null
        : !ServerMode ? $"{SiteName} runs on your server, not on this PC. Turn on Server Support in Plugins and download on the server."
        : ServerProblem is { } problem ? $"{_app.Queue.ServerDisplay ?? "Your server"} couldn't run {SiteName}: {problem}"
        : $"{_app.Queue.ServerDisplay ?? "The server"} can't be reached right now ({_app.Queue.StatusDetail}). Pages are read there.";

    /// <summary>What the server said was wrong with the browsed site's plugin, when it couldn't run it.</summary>
    private string? ServerProblem =>
        Plugin is { } plugin ? _app.Queue.ServerPlugins.FirstOrDefault(there => there.Id == plugin.Stored.Manifest.Id)?.Problem : null;

    /// <summary>The albums read so far for <see cref="AlbumsSearch"/> (pages 1 to <see cref="AlbumsPage"/>), or null before any.</summary>
    public IReadOnlyList<AlbumCard>? Albums => _albums;

    /// <summary>What <see cref="Albums"/> were searched for; "" for the latest.</summary>
    public string AlbumsSearch { get; private set; } = "";

    public int AlbumsPage { get; private set; }

    /// <summary>The last page read was the last there is.</summary>
    public bool AlbumsEnded { get; private set; }

    /// <summary>
    /// Read the first page of albums for <paramref name="search"/>, or with <paramref name="more"/> the next page of
    /// <see cref="AlbumsSearch"/>. Returns the albums it adds: an album the site moved onto the next page while it was
    /// read is there once.
    /// </summary>
    /// <param name="fresh">read the page again even when it was read in the last <see cref="ListKeep"/></param>
    /// <exception cref="StageException">it can't be read now, or couldn't be</exception>
    public async Task<IReadOnlyList<AlbumCard>> ReadAlbumsAsync(string search, bool more, CancellationToken cancel, bool fresh = false)
    {
        var list = Reader();
        (search, var page) = more && _albums is not null ? (AlbumsSearch, AlbumsPage + 1) : (search.Trim(), 1);
        var query = new AlbumQuery(search, page);
        var now = TimeProvider.System.GetUtcNow();
        IReadOnlyList<AlbumCard> read;
        if (!fresh && _listPages.TryGetValue(query, out var kept) && now - kept.At < ListKeep)
            read = kept.Albums;
        else
        {
            read = await list.AlbumsAsync(query, cancel);
            if (_listPages.Count >= MaxListPages)
                _listPages.Clear();
            _listPages[query] = (read, now);
        }
        var albums = page == 1 || _albums is null ? [] : _albums;
        var known = albums.Select(album => album.Link.Slug).ToHashSet(StringComparer.Ordinal);
        var added = read.Where(album => known.Add(album.Link.Slug)).ToList();
        albums.AddRange(added);
        _albums = albums;
        (AlbumsSearch, AlbumsPage) = (search, page);
        // a page shows up to its site's count (18 of 20, say), so only a page with none, or none new, is past the last
        AlbumsEnded = read.Count == 0 || (page > 1 && added.Count == 0);
        if (_pictures.Count > MaxPictures)
            _pictures.Clear(); // read again when shown again
        return added;
    }

    /// <summary>
    /// An album or file page: one read in the last <see cref="PageKeep"/>, or being read now (to look ahead, say), is the
    /// same read. A caller giving up doesn't stop it for the others.
    /// </summary>
    /// <param name="fresh">read the page again even when it was read lately</param>
    /// <exception cref="StageException">it can't be read now, or couldn't be</exception>
    public Task<BrowsedPage> OpenAsync(PageLink link, CancellationToken cancel, bool fresh = false)
    {
        var browser = Reader();
        var now = TimeProvider.System.GetUtcNow();
        if (fresh || !_pages.TryGetValue(link.Slug, out var kept) || now - kept.At >= PageKeep || kept.Read.IsFaulted || kept.Read.IsCanceled)
        {
            if (_pages.Count >= MaxPages)
                _pages.Clear();
            kept = (browser.OpenAsync(link, CancellationToken.None), now);
            _pages[link.Slug] = kept;
        }
        return kept.Read.WaitAsync(cancel);
    }

    /// <summary>Start reading a page that is likely to be opened; what goes wrong is only seen if it is.</summary>
    public void ReadAhead(PageLink link)
    {
        if (Browser is not null)
            _ = OpenAsync(link, CancellationToken.None).ContinueWith(read => _ = read.Exception, TaskScheduler.Default);
    }

    /// <summary>An album's files and size from its own page, once per run.</summary>
    /// <exception cref="StageException">it couldn't be read</exception>
    public async Task<AlbumDetails> DetailsAsync(PageLink album, CancellationToken cancel)
    {
        if (_details.TryGetValue(album.Slug, out var known))
            return known;
        return _details[album.Slug] = await Reader().DetailsAsync(album, cancel);
    }

    /// <summary>An album's or file's picture, once per run; kept in memory only.</summary>
    /// <exception cref="StageException">it couldn't be read</exception>
    public async Task<byte[]> ThumbnailAsync(Uri picture, CancellationToken cancel)
    {
        if (_pictures.TryGetValue(picture, out var known))
            return known;
        return _pictures[picture] = await Reader().ThumbnailAsync(picture, cancel);
    }

    /// <exception cref="StageException">nothing can be read now; the message says why</exception>
    private RemotePageBrowser Reader() => Browser ?? throw new StageException(Stage.Server, Unavailable ?? "");

    /// <summary>The reader or the watcher changed, or what the watcher watches did; on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Follow the queue's connection, and the plugins as they are installed and switched, from now on.</summary>
    public void Start()
    {
        _app.Queue.QueueReplaced += Follow;
        _app.Queue.StatusChanged += Follow;
        _app.SettingsChanged += Follow;
        Follow();
    }

    /// <summary>The queue's connection or the plugins changed: use the server's reader and watcher while there is a site to browse there.</summary>
    private void Follow()
    {
        var remote = ServerMode && Plugin is not null ? _app.Queue.Queue as RemoteQueue : null;
        if (!ReferenceEquals(remote, _remote))
            _ = UseRemoteAsync(remote);
        Changed?.Invoke();
    }

    private async Task UseRemoteAsync(RemoteQueue? remote)
    {
        if (_remoteWatcher is { } old)
        {
            old.Changed -= OnWatcherChanged;
            await old.DisposeAsync();
        }
        if (_remote is { } previous)
            previous.ConnectionChanged -= OnConnectionChanged;
        _remote = remote;
        _remoteBrowser = remote is null ? null : new RemotePageBrowser(remote);
        _remoteWatcher = remote is null ? null : new RemoteAlbumWatcher(remote);
        if (remote is null)
            return;
        remote.ConnectionChanged += OnConnectionChanged;
        _remoteWatcher!.Changed += OnWatcherChanged;
    }

    private void OnConnectionChanged(bool attached) => OnWatcherChanged();

    public async ValueTask DisposeAsync()
    {
        _app.Queue.QueueReplaced -= Follow;
        _app.Queue.StatusChanged -= Follow;
        _app.SettingsChanged -= Follow;
        await UseRemoteAsync(null);
    }

    private void OnWatcherChanged()
    {
        if (_ui.HasThreadAccess)
            Changed?.Invoke();
        else
            _ui.TryEnqueue(() => Changed?.Invoke());
    }

    /// <summary>The watcher there is while the server hasn't been reached: it watches nothing, and says why it can't start.</summary>
    private sealed class NoWatcher : IAlbumWatcher
    {
        private const string NoServer = "Albums are watched by your server, which isn't connected.";

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<WatchedAlbum> Albums => [];

        public WatchedAlbum? Find(PageLink link) => null;

        public Task WatchAsync(BrowsedPage album) => throw new StageException(Stage.Server, NoServer);

        public Task UnwatchAsync(PageLink link) => throw new StageException(Stage.Server, NoServer);

        public Task MarkSeenAsync(BrowsedPage album) => Task.CompletedTask;

        public Task CheckNowAsync(CancellationToken cancel) => throw new StageException(Stage.Server, NoServer);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
