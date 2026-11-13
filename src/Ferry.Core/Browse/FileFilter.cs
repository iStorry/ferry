using Ferry.Core.Files;

namespace Ferry.Core.Browse;

public enum FileSort
{
    /// <summary>As the page lists them.</summary>
    Page,
    Name,
    Size,
    Uploaded,
}

/// <summary>Sizes from <paramref name="Min"/> up to, not including, <paramref name="Max"/>; null for no bound.</summary>
public readonly record struct SizeRange(long? Min, long? Max)
{
    public static SizeRange Any { get; } = new(null, null);

    /// <summary>An unknown size is only in a range without bounds: nothing says it is in one.</summary>
    public bool Contains(long? size) =>
        (Min, Max) is (null, null) || size is { } known && (Min is not { } min || known >= min) && (Max is not { } max || known < max);
}

/// <summary>Which of a page's files the Browser shows, and in what order.</summary>
/// <param name="Text">words that must all be in a file's name, in any case; blank for any name</param>
/// <param name="Kinds">the kinds shown; empty for every kind</param>
/// <param name="HideInDownloads">leave out files already in the Downloads queue</param>
public sealed record FileFilter(string Text, IReadOnlySet<FileKind> Kinds, SizeRange Size, bool HideInDownloads,
                                FileSort Sort, bool Descending)
{
    public static FileFilter None { get; } = new("", new HashSet<FileKind>(), SizeRange.Any, false, FileSort.Page, false);

    /// <summary>Whether anything leaves files out (sorting doesn't).</summary>
    public bool IsActive => Words.Length > 0 || Kinds.Count > 0 || Size != SizeRange.Any || HideInDownloads;

    private string[] Words => Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <param name="inDownloads">the slugs of files in the Downloads queue</param>
    public IReadOnlyList<BrowsedFile> Apply(IReadOnlyList<BrowsedFile> files, IReadOnlySet<string> inDownloads)
    {
        var words = Words;
        var shown = files.Where(file => words.All(word => file.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase))
                                        && (Kinds.Count == 0 || Kinds.Contains(FileKinds.Of(file.Name)))
                                        && Size.Contains(file.Size)
                                        && !(HideInDownloads && inDownloads.Contains(file.Link.Key)));
        return [.. Order(shown)];
    }

    private IEnumerable<BrowsedFile> Order(IEnumerable<BrowsedFile> files) => (Sort, Descending) switch
    {
        (FileSort.Page, false) => files,
        (FileSort.Page, true) => files.Reverse(),
        (FileSort.Name, _) => By(files, file => file.Name, StringComparer.CurrentCultureIgnoreCase),
        // unknown sizes and dates count as the smallest and oldest
        (FileSort.Size, _) => By(files, file => file.Size ?? -1, Comparer<long>.Default),
        _ => By(files, file => file.Uploaded ?? DateTimeOffset.MinValue, Comparer<DateTimeOffset>.Default),
    };

    private IEnumerable<BrowsedFile> By<TKey>(IEnumerable<BrowsedFile> files, Func<BrowsedFile, TKey> key, IComparer<TKey> comparer) =>
        Descending ? files.OrderByDescending(key, comparer) : files.OrderBy(key, comparer);
}
