using Ferry.Core;
namespace Ferry.Core.Browse;

/// <summary>An album as its site lists it: its link, its name, how many files it says it has, and its picture.</summary>
/// <param name="Files">the count the card shows, or null if it shows none</param>
/// <param name="Thumbnail">the card's picture, or null if it has none</param>
public sealed record AlbumCard(PageLink Link, string Title, int? Files, Uri? Thumbnail);

/// <summary>Which albums to list: those matching <paramref name="Search"/> ("" for the latest), page <paramref name="Page"/> of them.</summary>
public sealed record AlbumQuery(string Search = "", int Page = 1)
{
    /// <summary>A search longer than this isn't sent.</summary>
    public const int MaxSearch = 200;

    public static AlbumQuery Latest { get; } = new();
}

/// <summary>What an album's own page says about all of it ("(2.13 GB) 663 Files"); null for what it doesn't say.</summary>
public sealed record AlbumDetails(int? Files, long? Size);

/// <summary>
/// Reads the albums a site lists, for the Browser page's home: a plugin does on the server, and the PC asks the server.
/// </summary>
public interface IAlbumDirectory
{
    /// <exception cref="StageException">the list could not be read; <see cref="StageException.Stage"/> says where</exception>
    Task<IReadOnlyList<AlbumCard>> AlbumsAsync(AlbumQuery query, CancellationToken cancel);

    /// <summary>An album's picture, as image bytes; only one on a list read before is read.</summary>
    /// <exception cref="StageException">it isn't on that list, or could not be read</exception>
    Task<byte[]> ThumbnailAsync(Uri picture, CancellationToken cancel);

    /// <summary>How many files an album has and how large it is, from its own page.</summary>
    /// <exception cref="StageException">not an album, or its page could not be read</exception>
    Task<AlbumDetails> DetailsAsync(PageLink album, CancellationToken cancel);
}
