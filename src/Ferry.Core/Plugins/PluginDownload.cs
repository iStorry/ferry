using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ferry.Core.Http;

namespace Ferry.Core.Plugins;

/// <summary>A file of a GitHub release, as its download link names it: github.com/{owner}/{repo}/releases/download/{tag}/{name}.</summary>
public sealed partial record GitHubReleaseFile(string Owner, string Repo, string Tag, string Name)
{
    /// <summary>The link as a release's file; null when it isn't one.</summary>
    public static GitHubReleaseFile? TryRead(Uri link) =>
        link.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && DownloadPath().Match(link.AbsolutePath) is { Success: true } match
            ? new GitHubReleaseFile(Part(match, "owner"), Part(match, "repo"), Part(match, "tag"), Part(match, "name"))
            : null;

    private static string Part(Match match, string name) => Uri.UnescapeDataString(match.Groups[name].Value);

    [GeneratedRegex(@"^/(?<owner>[^/]+)/(?<repo>[^/]+)/releases/download/(?<tag>[^/]+)/(?<name>[^/]+)$")]
    private static partial Regex DownloadPath();
}

/// <summary>A plugin's package from a link, brought to a file to be read like any other. Nothing of it is trusted for having been downloaded.</summary>
/// <param name="api">GitHub's API root; tests point it at a fake</param>
public sealed class PluginDownload(HttpFetcher http, Uri? api = null)
{
    private const long MaxBytes = 64L << 20;
    private const long MaxAnswerBytes = 4 << 20;

    private readonly Uri _api = api ?? new Uri("https://api.github.com/");

    /// <summary><paramref name="text"/> as a link a package can come from; false, with the reason, when it isn't one.</summary>
    public static bool TryReadLink(string text, out Uri link, out string reason)
    {
        var isLink = Uri.TryCreate(text.Trim(), UriKind.Absolute, out link!);
        reason = !isLink ? "That isn't a link."
            : link.Scheme != Uri.UriSchemeHttps ? "A plugin is only taken from an https link."
            : "";
        return reason.Length == 0;
    }

    /// <summary>Download <paramref name="link"/> as anyone can.</summary>
    /// <returns>the file it was brought to, in <paramref name="folder"/>, for the caller to delete</returns>
    /// <exception cref="StageException">it couldn't be downloaded (<see cref="Stage.Update"/>); <see cref="StageException.Status"/> is 404 for
    /// a file that isn't there, which is also what GitHub says of a private repository's</exception>
    /// <exception cref="PluginException">it is larger than a plugin may be</exception>
    public Task<string> FetchAsync(Uri link, string folder, CancellationToken cancel) =>
        SaveAsync(() => new HttpRequestMessage(HttpMethod.Get, link), folder, cancel);

    /// <summary>
    /// Download a file of a release in a repository only some may read, with an access token of the reader's. The token goes
    /// to GitHub's API and nowhere else: where the API sends on to for the bytes gets none of it.
    /// </summary>
    /// <exception cref="StageException">the token doesn't open the repository, or the release or the file isn't there (<see cref="Stage.Update"/>)</exception>
    /// <exception cref="PluginException">it is larger than a plugin may be</exception>
    public async Task<string> FetchAsync(GitHubReleaseFile file, string token, string folder, CancellationToken cancel)
    {
        var release = new Uri(_api, $"repos/{Uri.EscapeDataString(file.Owner)}/{Uri.EscapeDataString(file.Repo)}/releases/tags/{Uri.EscapeDataString(file.Tag)}");
        Uri asset;
        try
        {
            using var response = await http.SendAsync(Asked(release, token, "application/vnd.github+json"), Stage.Update, cancel);
            using var answer = JsonDocument.Parse(await HttpFetcher.ReadTextAsync(response, MaxAnswerBytes, Stage.Update, cancel));
            asset = AssetUrl(answer.RootElement, file.Name)
                    ?? throw new StageException(Stage.Update, $"Release {file.Tag} of {file.Owner}/{file.Repo} has no file called {file.Name}.");
        }
        catch (StageException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new StageException(Stage.Update, $"GitHub didn't open release {file.Tag} of {file.Owner}/{file.Repo} for that token. It needs to be one that can read that repository's contents.", inner: e);
        }
        catch (JsonException e)
        {
            throw new StageException(Stage.Update, "GitHub's answer about the release couldn't be read.", inner: e);
        }
        return await SaveAsync(() => Asked(asset, token, "application/octet-stream"), folder, cancel);
    }

    /// <summary>Where the API serves the release's file called <paramref name="name"/>; null when it has none.</summary>
    private Uri? AssetUrl(JsonElement release, string name)
    {
        if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var called) && called.GetString() == name
                && asset.TryGetProperty("url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var served)
                // only ever the API itself: an answer naming another host would be handed the token
                && served.Host.Equals(_api.Host, StringComparison.OrdinalIgnoreCase) && served.Scheme == _api.Scheme)
                return served;
        }
        return null;
    }

    private static HttpRequestMessage Asked(Uri url, string token, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"{Product.Name}/{Product.Version}");
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<string> SaveAsync(Func<HttpRequestMessage> ask, string folder, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + PluginPackage.Extension);
        try
        {
            using var request = ask();
            using var response = await http.SendAsync(request, Stage.Update, cancel, asFile: true);
            await using var body = await response.Content.ReadAsStreamAsync(cancel);
            await using var to = File.Create(file);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, cancel)) > 0)
            {
                if ((done += read) > MaxBytes)
                    throw new PluginException($"It is larger than {MaxBytes >> 20} MB, the most a plugin may be.");
                await to.WriteAsync(buffer.AsMemory(0, read), cancel);
            }
            return file;
        }
        catch (Exception e)
        {
            File.Delete(file);
            throw e is HttpRequestException or IOException
                ? new StageException(Stage.Update, $"The plugin couldn't be downloaded: {e.Message}", inner: e)
                : e;
        }
    }
}
