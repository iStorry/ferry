using Ferry.Core;
using Ferry.Core.Files;

namespace Ferry.Core.Browse;

/// <summary>
/// The server's watcher: reads the watched albums with <paramref name="browser"/> every <paramref name="every"/> while
/// the agent runs, one album at a time, and keeps the list in watched.json.
/// </summary>
/// <param name="mayRead">whether albums may be read now; while it says no the list is kept and nothing is checked. Always when null.</param>
/// <param name="problem">told what went wrong beyond an album that couldn't be read: the list couldn't be saved, a check failed in an unforeseen way</param>
public sealed class AlbumWatcher(IPageBrowser browser, string path, TimeSpan every, TimeProvider clock, Func<bool>? mayRead = null,
                                 Action<string, Exception>? problem = null) : IAlbumWatcher
{
    private readonly JsonListFile<WatchedAlbum> _file = new(path, Json.FerryFileJson.Default.ListWatchedAlbum);
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private List<WatchedAlbum> _albums = [];
    private PeriodicTimer? _timer;
    private Task _loop = Task.CompletedTask;

    public event Action? Changed;

    public IReadOnlyList<WatchedAlbum> Albums
    {
        get
        {
            lock (_lock)
                return _albums;
        }
    }

    /// <summary>Load the watched albums and start checking them.</summary>
    /// <exception cref="IOException">watched.json is there but can't be opened</exception>
    public void Start()
    {
        lock (_lock)
            _albums = _file.Load();
        _timer = new PeriodicTimer(every, clock);
        _loop = RunAsync(_timer);
    }

    public WatchedAlbum? Find(PageLink link) => Albums.FirstOrDefault(album => album.Slug == link.Slug);

    public Task WatchAsync(BrowsedPage album)
    {
        if (album.Link.Kind != PageKind.Album)
            throw new ArgumentException("Only albums can be watched.", nameof(album));
        var watched = new WatchedAlbum(album.Link.Original.ToString(), album.Link.Slug, album.Title, Slugs(album), 0,
                                       clock.GetUtcNow(), null);
        Change(albums => albums.Any(a => a.Slug == watched.Slug) ? albums : [.. albums, watched]);
        return Task.CompletedTask;
    }

    public Task UnwatchAsync(PageLink link)
    {
        Change(albums => [.. albums.Where(a => a.Slug != link.Slug)]);
        return Task.CompletedTask;
    }

    public Task MarkSeenAsync(BrowsedPage album)
    {
        Update(album.Link.Slug, watched => watched with { Title = album.Title, Seen = Slugs(album), NewFiles = 0 });
        return Task.CompletedTask;
    }

    public async Task CheckNowAsync(CancellationToken cancel)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stop.Token);
        await _checking.WaitAsync(both.Token);
        try
        {
            foreach (var album in Albums)
            {
                if (mayRead?.Invoke() == false)
                    return;
                await CheckAsync(album, both.Token);
            }
        }
        finally
        {
            _checking.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _timer?.Dispose(); // ends the loop's wait for the next tick
        await _loop;
        _stop.Dispose();
        _checking.Dispose();
    }

    private async Task RunAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync())
                await CheckNowAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return; // stopped in the middle of a check
        }
    }

    /// <summary>One album; whatever goes wrong with it is written on it, and the next album is checked all the same.</summary>
    private async Task CheckAsync(WatchedAlbum album, CancellationToken cancel)
    {
        BrowsedPage page;
        try
        {
            page = await browser.OpenAsync(album.ToPageLink(), cancel);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (e is not StageException)
                problem?.Invoke($"checking the album {album.Slug}", e);
            Update(album.Slug, watched => watched with { CheckedAt = clock.GetUtcNow(), Problem = e.Message });
            return;
        }
        // counted against what is seen now: the user may have opened the album while it was being read
        Update(album.Slug, watched =>
        {
            var seen = watched.Seen.ToHashSet(StringComparer.Ordinal);
            return watched with
            {
                Title = page.Title,
                NewFiles = page.Files.Count(file => !seen.Contains(file.Link.Key)),
                CheckedAt = clock.GetUtcNow(),
                Problem = null,
            };
        });
    }

    /// <summary>Change the album with that slug, if it is still watched.</summary>
    private void Update(string slug, Func<WatchedAlbum, WatchedAlbum> change) =>
        Change(albums => [.. albums.Select(album => album.Slug == slug ? change(album) : album)]);

    private void Change(Func<List<WatchedAlbum>, List<WatchedAlbum>> change)
    {
        lock (_lock)
        {
            _albums = change(_albums);
            try
            {
                _file.Save(_albums);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // held for a moment (an antivirus, a sync tool): the list is right in memory, and the next change saves it
                problem?.Invoke("saving the watched albums", e);
            }
        }
        Changed?.Invoke();
    }

    private static List<string> Slugs(BrowsedPage page) => [.. page.Files.Select(file => file.Link.Key)];
}
