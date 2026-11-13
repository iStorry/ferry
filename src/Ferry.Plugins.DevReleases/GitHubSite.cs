using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Json;
using Ferry.Core.Sites;

namespace Ferry.Plugins.DevReleases;

/// <summary>
/// GitHub's pages about a file ("/blob/") and about a release ("/releases/latest", "/releases/tag/..."). Links to a release's
/// file itself aren't this site's: they already are the file.
/// </summary>
/// <param name="system">whose file a link to a whole release means</param>
public sealed partial class GitHubSite(HttpFetcher http, string userAgent, TargetSystem system) : ISite
{
    public const string SiteId = "github";

    private const string Who = "GitHub";

    private static readonly HostSet Hosts = HostSet.Of("github.com");
    private static readonly Uri Site = new("https://github.com/");
    private static readonly Uri Api = new("https://api.github.com/repos/");

    /// <summary>A release's file, as the API describes it.</summary>
    internal sealed record Asset(long Id, string Name, long? Size, Uri Url);

    public string Id => SiteId;

    public string Name => Who;

    public bool Claims(Uri link) => Hosts.Claims(link) && (FilePage().IsMatch(link.AbsolutePath) || ReleasePage().IsMatch(link.AbsolutePath));

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = new FileLink(SiteId, link, link.AbsolutePath, ReleasePage().Match(link.AbsolutePath) is { Success: true } release ? $"{release.Groups["repo"].Value} release" : null);
        reason = null;
        return true;
    }

    public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        if (ReleasePage().Match(file.Key) is { Success: true } release)
            return FromReleaseAsync(file, release, cancel);
        var page = FilePage().Match(file.Key);
        return LinkProbe.ResolveFileAsync(http, userAgent, file, new Uri(Site, $"{page.Groups["repo"].Value}/raw/{page.Groups["file"].Value}"), details,
                                          "GitHub sent a web page, not the file. The project may be private, or the file may have moved.", cancel);
    }

    private Task<ResolvedFile> FromReleaseAsync(FileLink file, Match page, CancellationToken cancel) =>
        Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, async tryCancel =>
        {
            var assets = Read(await AskAsync(page, tryCancel));
            var chosen = ReleaseAssets.Pick([.. assets.Select(asset => asset.Name)], system);
            var asset = assets.First(asset => asset.Name == chosen);
            // the file itself: a new release under "latest" is a new file, not more of this one
            return new ResolvedFile(file, asset.Id.ToString(CultureInfo.InvariantCulture), FileNames.Sanitize(chosen), asset.Url, Referer: null, userAgent,
                                    ExpiresAt: null, IsWebPage: false, asset.Size);
        }, cancel);

    private async Task<string> AskAsync(Match page, CancellationToken cancel)
    {
        var release = page.Groups["tag"].Success ? $"tags/{page.Groups["tag"].Value}" : "latest";
        try
        {
            return await http.GetTextAsync(new Uri(Api, $"{page.Groups["repo"].Value}/releases/{release}"), userAgent, HttpAnswers.MaxApiBytes, Stage.Resolve, cancel);
        }
        catch (StageException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new StageException(Stage.Resolve, "GitHub only answers 60 of these an hour without a login, and that has run out. Try again later, or copy the link of the file you want.", inner: e, status: e.Status);
        }
        catch (StageException e) when (e.Status is HttpStatusCode.NotFound)
        {
            throw new StageException(Stage.Resolve, "GitHub has no such release. The project may have none yet, or may be private.", inner: e, status: e.Status);
        }
    }

    /// <summary>The release's files that have a name and a link.</summary>
    internal static IReadOnlyList<Asset> Read(string text)
    {
        using var json = JsonAnswer.Parse(text, Who);
        if (!json.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return [];
        var read = new List<Asset>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } called
                && asset.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                && asset.TryGetProperty("browser_download_url", out var link) && Uri.TryCreate(link.GetString(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
            {
                read.Add(new Asset(id.GetInt64(), called, asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null, url));
            }
        }
        return read;
    }

    [GeneratedRegex("^/(?<repo>[^/]+/[^/]+)/blob/(?<file>.+)$")]
    private static partial Regex FilePage();

    [GeneratedRegex("^/(?<repo>[^/]+/[^/]+)/releases/(?:latest|tag/(?<tag>.+?))/?$")]
    private static partial Regex ReleasePage();
}
