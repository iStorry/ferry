using Ferry.Core;
namespace Ferry.Core.Browse;

/// <summary>An album being watched for new files.</summary>
/// <param name="Seen">the slugs of the files the user has seen; any other file on the album is new</param>
/// <param name="NewFiles">files on the album at the last check that aren't in <paramref name="Seen"/></param>
/// <param name="Problem">why the last check couldn't read the album, or null if it could</param>
public sealed record WatchedAlbum(string Link, string Slug, string Title, IReadOnlyList<string> Seen, int NewFiles,
                                  DateTimeOffset? CheckedAt, string? Problem)
{
    public PageLink ToPageLink() => new(new Uri(Link), PageKind.Album, Slug);
}

/// <summary>
/// Reads watched albums again now and then and counts the files added since the user last looked. The server's agent
/// watches, also while the PC is off; the PC's watcher is the agent's, asked over the connection.
/// </summary>
/// <remarks>
/// The PC's changes go through the agent: they fail with a <see cref="StageException"/> while it can't be reached, and an
/// <see cref="InvalidOperationException"/> when it refuses, saying why.
/// </remarks>
public interface IAlbumWatcher : IAsyncDisposable
{
    /// <summary>How often watched albums are read again.</summary>
    static TimeSpan Every { get; } = TimeSpan.FromMinutes(30);

    /// <summary>Raised on any thread after <see cref="Albums"/> changed.</summary>
    event Action? Changed;

    IReadOnlyList<WatchedAlbum> Albums { get; }

    /// <summary>The album watched under the same slug, whichever mirror its link named.</summary>
    WatchedAlbum? Find(PageLink link);

    /// <summary>Watch an album, with every file on it now counted as seen.</summary>
    /// <exception cref="ArgumentException">the page is a file, not an album</exception>
    Task WatchAsync(BrowsedPage album);

    Task UnwatchAsync(PageLink link);

    /// <summary>The user has seen every file on the album as it is now: none is new any more.</summary>
    Task MarkSeenAsync(BrowsedPage album);

    /// <summary>Read every watched album now instead of at the next check.</summary>
    Task CheckNowAsync(CancellationToken cancel);
}
