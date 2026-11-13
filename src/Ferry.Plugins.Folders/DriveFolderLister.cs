using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Folders;

/// <summary>A shared Google Drive folder and the folders in it, read from the plain page Drive offers for embedding one.</summary>
public sealed partial class DriveFolderLister(HttpFetcher http, string userAgent) : IFileLister
{
    private const int MostDepth = 5;
    private const int MostFiles = 2000;

    private static readonly Uri Listing = new("https://drive.google.com/embeddedfolderview");

    public async Task<FileList?> TryListAsync(Uri link, RequestDetails details, CancellationToken cancel)
    {
        if (!string.Equals(link.Host, "drive.google.com", StringComparison.OrdinalIgnoreCase) || FolderPath().Match(link.AbsolutePath) is not { Success: true } folder)
            return null;
        var (files, seen) = (new List<string>(), new HashSet<string>());
        var title = await ReadFolderAsync(folder.Groups["id"].Value, files, seen, depth: 0, cancel);
        return files.Count > 0
            ? new FileList(title, files)
            : throw new StageException(Stage.Link, "No files could be read from this Google Drive folder. It may be private, or hold only Google documents.");
    }

    private async Task<string?> ReadFolderAsync(string id, List<string> files, HashSet<string> seen, int depth, CancellationToken cancel)
    {
        if (depth > MostDepth || !seen.Add(id))
            return null;
        var page = await HtmlLinks.ReadPageAsync(http, userAgent, UrlQuery.With(Listing, ("id", id)), RequestDetails.None, cancel);
        var (found, folders) = Read(page);
        files.AddRange(found.Take(MostFiles - files.Count));
        foreach (var inner in folders.TakeWhile(_ => files.Count < MostFiles))
            await ReadFolderAsync(inner, files, seen, depth + 1, cancel);
        return HtmlLinks.Title(page);
    }

    /// <summary>The links to files on a listing page, and the ids of the folders on it.</summary>
    internal static (IReadOnlyList<string> Files, IReadOnlyList<string> Folders) Read(string page)
    {
        var hrefs = HtmlLinks.Hrefs(page).ToList();
        return ([.. hrefs.Where(href => FilePath().IsMatch(href)).Distinct()],
                [.. hrefs.Select(href => FolderPath().Match(href)).Where(match => match.Success).Select(match => match.Groups["id"].Value).Distinct()]);
    }

    [GeneratedRegex(@"/folders/(?<id>[A-Za-z0-9_-]+)")]
    private static partial Regex FolderPath();

    [GeneratedRegex(@"^https://drive\.google\.com/file/d/")]
    private static partial Regex FilePath();
}
