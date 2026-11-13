using Ferry.Core;
using Ferry.Core.Browse;

namespace Ferry.Plugins.Server;

/// <summary>The PC's reader: the agent reads the page, or the list of albums, on the server and says what is on it.</summary>
public sealed class RemotePageBrowser(RemoteQueue agent) : IPageBrowser, IAlbumDirectory
{
    // an album of many pages, each read with retries, takes longer than an ordinary request may
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(10);

    public async Task<BrowsedPage> OpenAsync(PageLink link, CancellationToken cancel) =>
        (await ReadAsync(new AgentRequest(0, AgentCommand.Browse, Link: link.Original.ToString()), cancel)).Page
        ?? throw new StageException(Stage.Server, "The server answered without the page.");

    public async Task<IReadOnlyList<AlbumCard>> AlbumsAsync(AlbumQuery query, CancellationToken cancel) =>
        (await ReadAsync(new AgentRequest(0, AgentCommand.Albums, Query: query), cancel)).Cards
        ?? throw new StageException(Stage.Server, "The server answered without the albums.");

    public async Task<byte[]> ThumbnailAsync(Uri picture, CancellationToken cancel) =>
        (await ReadAsync(new AgentRequest(0, AgentCommand.Thumbnail, Link: picture.ToString()), cancel)).Picture
        ?? throw new StageException(Stage.Server, "The server answered without the picture.");

    public async Task<AlbumDetails> DetailsAsync(PageLink album, CancellationToken cancel) =>
        (await ReadAsync(new AgentRequest(0, AgentCommand.AlbumDetails, Link: album.Original.ToString()), cancel)).Details
        ?? throw new StageException(Stage.Server, "The server answered without the album's details.");

    private async Task<AgentMessage> ReadAsync(AgentRequest request, CancellationToken cancel)
    {
        try
        {
            return await agent.RequestAsync(request, ReadTimeout, cancel);
        }
        catch (InvalidOperationException e)
        {
            throw new StageException(Stage.Server, e.Message, inner: e); // the agent couldn't: for the user as any failure to read
        }
    }
}

/// <summary>
/// The PC's watcher: the agent watches, also while this PC is off. The albums are the agent's, as it last said;
/// changes go to the agent, and come back from it to every connected PC.
/// </summary>
/// <remarks>While the server can't be reached the last albums stay, and changes fail with a <see cref="StageException"/>.</remarks>
public sealed class RemoteAlbumWatcher : IAlbumWatcher
{
    // reading every watched album, one at a time
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(30);

    private readonly RemoteQueue _agent;
    private readonly Lock _lock = new();
    private IReadOnlyList<WatchedAlbum> _albums = [];

    public RemoteAlbumWatcher(RemoteQueue agent)
    {
        _agent = agent;
        _agent.WatchedChanged += OnWatchedChanged;
    }

    public event Action? Changed;

    public IReadOnlyList<WatchedAlbum> Albums
    {
        get
        {
            lock (_lock)
                return _albums;
        }
    }

    public WatchedAlbum? Find(PageLink link) => Albums.FirstOrDefault(album => album.Slug == link.Slug);

    public Task WatchAsync(BrowsedPage album) =>
        album.Link.Kind == PageKind.Album
            ? _agent.RequestAsync(new AgentRequest(0, AgentCommand.Watch, Page: album))
            : throw new ArgumentException("Only albums can be watched.", nameof(album));

    public Task UnwatchAsync(PageLink link) =>
        _agent.RequestAsync(new AgentRequest(0, AgentCommand.Unwatch, Link: link.Original.ToString()));

    public Task MarkSeenAsync(BrowsedPage album) =>
        _agent.RequestAsync(new AgentRequest(0, AgentCommand.MarkSeen, Page: album));

    public Task CheckNowAsync(CancellationToken cancel) =>
        _agent.RequestAsync(new AgentRequest(0, AgentCommand.CheckWatched), CheckTimeout, cancel);

    public ValueTask DisposeAsync()
    {
        _agent.WatchedChanged -= OnWatchedChanged;
        return ValueTask.CompletedTask;
    }

    private void OnWatchedChanged(IReadOnlyList<WatchedAlbum> albums)
    {
        lock (_lock)
            _albums = albums;
        Changed?.Invoke();
    }
}
