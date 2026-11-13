using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Json;
using Ferry.Core.Sites;

namespace Ferry.Plugins.FileHosts;

/// <summary>Cyberdrop's file pages ("/f/{id}"): its API says what the file is called, and where to ask for a link that lasts a while.</summary>
internal sealed partial class CyberdropSite(HttpFetcher http, string userAgent) : ISite
{
    public const string SiteId = "cyberdrop";

    private const string Who = "Cyberdrop";

    private static readonly HostSet Hosts = HostSet.Of("cyberdrop.me", "cyberdrop.cc", "cyberdrop.to", "cyberdrop.nl", "cyberdrop.ch");
    private static readonly Uri FileInfoApi = new("https://api.cyberdrop.me/api/file/info/");

    public string Id => SiteId;

    public string Name => Who;

    public bool Claims(Uri link) => IsCyberdrop(link) && FilePage().IsMatch(link.AbsolutePath);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = new FileLink(SiteId, link, FilePage().Match(link.AbsolutePath).Groups["id"].Value, "Cyberdrop file");
        reason = null;
        return true;
    }

    public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel) =>
        Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, async tryCancel =>
        {
            var (name, ask) = ReadInfo(await http.GetTextAsync(new Uri(FileInfoApi, Uri.EscapeDataString(file.Key)), userAgent, HttpAnswers.MaxApiBytes, Stage.Resolve, tryCancel));
            var url = ReadUrl(await http.GetTextAsync(ask, userAgent, HttpAnswers.MaxApiBytes, Stage.Resolve, tryCancel));
            return new ResolvedFile(file, file.Key, FileNames.Sanitize(name ?? file.Key), url, new Uri(file.Original.GetLeftPart(UriPartial.Authority) + "/"), userAgent, ExpiresAt: null, IsWebPage: false, Size: null);
        }, cancel);

    internal static (string? Name, Uri Ask) ReadInfo(string text)
    {
        using var json = JsonAnswer.Parse(text, Who);
        return json.RootElement.TryGetProperty("auth_url", out var ask) && Uri.TryCreate(ask.GetString(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? (json.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null, url)
            : throw new StageException(Stage.Resolve, "Cyberdrop didn't say where this file is. It may have been removed, or its API may have changed.");
    }

    internal static Uri ReadUrl(string text)
    {
        using var json = JsonAnswer.Parse(text, Who);
        return json.RootElement.TryGetProperty("url", out var given) && Uri.TryCreate(given.GetString(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url
            : throw new StageException(Stage.Resolve, "Cyberdrop gave no link for this file.");
    }

    internal static bool IsCyberdrop(Uri link) => Hosts.Claims(link);

    [GeneratedRegex("^/f/(?<id>[A-Za-z0-9_-]+)/?$")]
    private static partial Regex FilePage();
}

/// <summary>A Cyberdrop album ("/a/{id}"): its page links to each file.</summary>
internal sealed partial class CyberdropLister(HttpFetcher http, string userAgent) : IFileLister
{
    public async Task<FileList?> TryListAsync(Uri link, RequestDetails details, CancellationToken cancel)
    {
        if (!CyberdropSite.IsCyberdrop(link) || !AlbumPage().IsMatch(link.AbsolutePath))
            return null;
        var list = Read(await HtmlLinks.ReadPageAsync(http, userAgent, link, RequestDetails.None, cancel), link);
        return list.Links.Count > 0 ? list : throw new StageException(Stage.Link, "No files could be read from this Cyberdrop album.");
    }

    /// <summary>The album's files, under its heading, or the page's title when the heading has moved.</summary>
    internal static FileList Read(string page, Uri link)
    {
        var files = HtmlLinks.Hrefs(page).Select(href => Uri.TryCreate(link, href, out var file) ? file : null).OfType<Uri>()
            .Where(file => CyberdropSite.IsCyberdrop(file) && file.AbsolutePath.StartsWith("/f/", StringComparison.Ordinal))
            .Select(file => file.ToString()).Distinct().ToList();
        var heading = AlbumHeading().Match(page) is { Success: true } found ? WebUtility.HtmlDecode(found.Groups["title"].Value).Trim() : null;
        return new FileList(heading ?? HtmlLinks.Title(page), files);
    }

    [GeneratedRegex("^/a/[A-Za-z0-9_-]+/?$")]
    private static partial Regex AlbumPage();

    [GeneratedRegex("""id="title"[^>]*>(?<title>[^<]+)<""")]
    private static partial Regex AlbumHeading();
}
