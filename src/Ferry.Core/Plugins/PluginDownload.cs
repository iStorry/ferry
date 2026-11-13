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

    /// <summary>What an access token for this file's repository is kept under, among the secrets.</summary>
    public string TokenSecret => "github-token-" + NotPlain().Replace($"{Owner}-{Repo}".ToLowerInvariant(), "-");

    private static string Part(Match match, string name) => Uri.UnescapeDataString(match.Groups[name].Value);

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex NotPlain();

    [GeneratedRegex(@"^/(?<owner>[^/]+)/(?<repo>[^/]+)/releases/download/(?<tag>[^/]+)/(?<name>[^/]+)$")]
    private static partial Regex DownloadPath();
}

/// <summary>A newer package of an installed plugin, and the release file it is.</summary>
public sealed record PluginUpdate(Version Version, GitHubReleaseFile File)
{
    /// <summary>The file's address as a browser has it, which is also what it is installed from.</summary>
    public Uri Link => new($"https://github.com/{File.Owner}/{File.Repo}/releases/download/{Uri.EscapeDataString(File.Tag)}/{Uri.EscapeDataString(File.Name)}");
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

    /// <summary>
    /// A newer package of a plugin than <paramref name="installed"/>, among the files of the repository's latest release:
    /// one named "{id}-{version}.ferryplugin", as the pack tool names them. Null when the latest has none newer.
    /// </summary>
    /// <param name="source">a release file of the repository, as the plugin was installed from</param>
    /// <param name="token">for a repository only some may read; null to ask as anyone can</param>
    /// <exception cref="StageException">GitHub couldn't be asked, or didn't open the repository (<see cref="Stage.Update"/>)</exception>
    public async Task<PluginUpdate?> NewerAsync(GitHubReleaseFile source, string pluginId, Version installed, string? token, CancellationToken cancel)
    {
        var latest = new Uri(_api, $"repos/{Uri.EscapeDataString(source.Owner)}/{Uri.EscapeDataString(source.Repo)}/releases/latest");
        try
        {
            using var response = await http.SendAsync(Asked(latest, token, "application/vnd.github+json"), Stage.Update, cancel);
            using var answer = JsonDocument.Parse(await HttpFetcher.ReadTextAsync(response, MaxAnswerBytes, Stage.Update, cancel));
            var release = answer.RootElement;
            if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { Length: > 0 } tagName
                || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            PluginUpdate? newest = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var called) && called.GetString() is { } name && PackageVersion(name, pluginId) is { } version
                    && version > installed && (newest is null || version > newest.Version))
                    newest = new PluginUpdate(version, source with { Tag = tagName, Name = name });
            }
            return newest;
        }
        catch (JsonException e)
        {
            throw new StageException(Stage.Update, "GitHub's answer about the latest release couldn't be read.", inner: e);
        }
    }

    /// <summary>The version in a package's name, "{id}-{version}.ferryplugin"; null when the name isn't that plugin's.</summary>
    internal static Version? PackageVersion(string name, string pluginId) =>
        name.StartsWith(pluginId + "-", StringComparison.Ordinal) && name.EndsWith(PluginPackage.Extension, StringComparison.Ordinal)
        && Version.TryParse(name[(pluginId.Length + 1)..^PluginPackage.Extension.Length], out var version)
            ? version
            : null;

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

    private static HttpRequestMessage Asked(Uri url, string? token, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"{Product.Name}/{Product.Version}");
        request.Headers.Accept.ParseAdd(accept);
        if (token is not null)
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
