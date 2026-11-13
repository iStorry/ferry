using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.OpenDirectories;

/// <summary>An Apache or nginx listing ("Index of /files/") and the folders under it, on the same server only.</summary>
public sealed partial class DirectoryLister(HttpFetcher http, string userAgent) : IFileLister
{
    private const int MostDepth = 4;
    private const int MostFiles = 5000;

    /// <summary>As much of a page as says whether it is a listing: its title and heading are at the top.</summary>
    internal const int HeadBytes = 16 << 10;

    public async Task<FileList?> TryListAsync(Uri link, RequestDetails details, CancellationToken cancel)
    {
        if (!LinkProbe.IsWebLink(link) || !link.AbsolutePath.EndsWith('/') || link.AbsolutePath == "/")
            return null;
        if (await ListingPageAsync(link, details, cancel) is not { } page)
            return null;
        var (files, seen) = (new List<string>(), new HashSet<string> { link.ToString() });
        await CollectAsync(link, page, files, seen, details, depth: 0, cancel);
        return files.Count > 0 ? new FileList(FileLink.LastSegment(link), files) : throw new StageException(Stage.Link, "This listing has no files.");
    }

    private async Task CollectAsync(Uri folder, string page, List<string> files, HashSet<string> seen, RequestDetails details, int depth, CancellationToken cancel)
    {
        var (found, folders) = Read(page, folder);
        files.AddRange(found.Take(MostFiles - files.Count).Select(file => file.ToString()));
        if (depth == MostDepth)
            return;
        foreach (var inner in folders.TakeWhile(_ => files.Count < MostFiles).Where(inner => seen.Add(inner.ToString())))
        {
            if (await ListingPageAsync(inner, details, cancel) is { } next)
                await CollectAsync(inner, next, files, seen, details, depth + 1, cancel);
        }
    }

    /// <summary>
    /// The listing page at <paramref name="folder"/>; null when what is there isn't one. A link that ends in "/" is as often a file,
    /// so only the start of the answer is asked for, and the rest only once that start says it is a listing.
    /// </summary>
    /// <exception cref="StageException">the server couldn't be asked, or refused</exception>
    private async Task<string?> ListingPageAsync(Uri folder, RequestDetails details, CancellationToken cancel)
    {
        var (head, whole) = await Retry.RunAsync(Stage.Link, LinkProbe.AskRetry, async tryCancel =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, folder);
            request.Headers.UserAgent.ParseAdd(userAgent);
            request.Headers.Range = new RangeHeaderValue(0, HeadBytes - 1);
            details.ApplyTo(request);
            using var response = await http.SendAsync(request, Stage.Link, tryCancel);
            return LinkProbe.IsWebPage(response)
                ? (await HttpFetcher.ReadTextAsync(response, HttpAnswers.MaxPageBytes, Stage.Link, tryCancel), IsWhole(response))
                : (null, false);
        }, cancel);
        if (head is null || !IsListing(head))
            return null;
        return whole ? head : await HtmlLinks.ReadPageAsync(http, userAgent, folder, details, cancel);
    }

    /// <summary>Whether the answer to asking for the start is the whole page: the server sent it all, or there was no more.</summary>
    private static bool IsWhole(HttpResponseMessage response) =>
        response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange is { Length: <= HeadBytes };

    internal static bool IsListing(string page) => ListingMark().IsMatch(page);

    /// <summary>The files and folders a listing page shows: what is under it, without its sort links or the way back up.</summary>
    internal static (IReadOnlyList<Uri> Files, IReadOnlyList<Uri> Folders) Read(string page, Uri folder)
    {
        var entries = HtmlLinks.Hrefs(page)
            .Select(href => Uri.TryCreate(folder, href, out var entry) ? entry : null).OfType<Uri>()
            .Where(entry => entry.Query.Length == 0 && entry.Fragment.Length == 0 && HttpFetcher.IsSameSite(entry, folder)
                            && entry.AbsolutePath.Length > folder.AbsolutePath.Length && entry.AbsolutePath.StartsWith(folder.AbsolutePath, StringComparison.Ordinal))
            .Distinct().ToList();
        return ([.. entries.Where(entry => !entry.AbsolutePath.EndsWith('/'))], [.. entries.Where(entry => entry.AbsolutePath.EndsWith('/'))]);
    }

    [GeneratedRegex(@"<(?:title|h1)[^>]*>\s*Index of\b", RegexOptions.IgnoreCase)]
    private static partial Regex ListingMark();
}
