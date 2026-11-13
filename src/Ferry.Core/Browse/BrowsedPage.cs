using Ferry.Core.Sites;

namespace Ferry.Core.Browse;

/// <summary>A file as an album or file page lists it.</summary>
/// <param name="Size">the size the page shows, or null if it shows none</param>
/// <param name="Uploaded">when the page says it was uploaded, or null if it doesn't say</param>
/// <param name="Thumbnail">its picture on the album page, or null if it shows none</param>
public sealed record BrowsedFile(FileLink Link, string Name, long? Size, DateTimeOffset? Uploaded, Uri? Thumbnail = null);

/// <summary>What an album or file page says: its title and the files on it (one, for a file page).</summary>
public sealed record BrowsedPage(PageLink Link, string Title, IReadOnlyList<BrowsedFile> Files)
{
    /// <summary>The files' sizes added up; null when there are none or a size is unknown.</summary>
    public long? TotalSize => Files.Count > 0 && Files.All(f => f.Size is not null) ? Files.Sum(f => f.Size ?? 0) : null;
}
