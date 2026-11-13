using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ferry.Core.Http;

namespace Ferry.Core.Updates;

/// <summary>A file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <param name="Tag">"v0.2.0"</param>
/// <param name="Notes">the release's text (Markdown)</param>
/// <param name="Page">the release's page on GitHub</param>
public sealed record Release(string Tag, Version Version, string Notes, Uri Page, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Newer than <paramref name="current"/> ("0.1.0", or "0.1.0+commit").</summary>
    public bool IsNewerThan(string current) =>
        Releases.TryParseVersion(current, out var running) && Version > running;
}

/// <summary>
/// Ferry's releases on GitHub (<see cref="Repository"/>): the latest one or one by version, and their files checked
/// against the release's SHA256SUMS.txt before anything uses them.
/// </summary>
/// <param name="api">GitHub's API root; tests point it at a fake</param>
public sealed class Releases(HttpFetcher http, Uri? api = null)
{
    public const string Repository = "iStorry/ferry";
    public const string ChecksumsName = "SHA256SUMS.txt";
    private const long MaxAnswerBytes = 4 << 20;

    private readonly Uri _api = api ?? new Uri("https://api.github.com/");

    public static Uri ReleasesPage { get; } = new($"https://github.com/{Repository}/releases");

    /// <summary>The newest published release, or null when there is none yet.</summary>
    /// <exception cref="StageException">GitHub can't be reached or answered with an error (<see cref="Stage.Update"/>)</exception>
    public Task<Release?> LatestAsync(CancellationToken cancel) => GetAsync($"repos/{Repository}/releases/latest", cancel);

    /// <summary>The release for <paramref name="version"/> ("0.2.0"), or null when there is none.</summary>
    public Task<Release?> ForVersionAsync(string version, CancellationToken cancel) =>
        GetAsync($"repos/{Repository}/releases/tags/v{Uri.EscapeDataString(version)}", cancel);

    /// <summary>
    /// Download <paramref name="asset"/> into <paramref name="folder"/> and check it against the release's
    /// SHA256SUMS.txt; a file that doesn't match is deleted.
    /// </summary>
    /// <returns>the downloaded file</returns>
    /// <exception cref="StageException">it couldn't be downloaded, or isn't the published file (<see cref="Stage.Update"/>)</exception>
    public async Task<string> DownloadAsync(Release release, ReleaseAsset asset, string folder, CancellationToken cancel,
                                            IProgress<double>? progress = null)
    {
        var sums = release.Asset(ChecksumsName)
                   ?? throw new StageException(Stage.Update, $"Release {release.Tag} has no {ChecksumsName}, so its files can't be checked.");
        var expected = ParseChecksums(await GetTextAsync(sums.Url, cancel)).GetValueOrDefault(asset.Name)
                       ?? throw new StageException(Stage.Update, $"{ChecksumsName} of {release.Tag} doesn't list {asset.Name}.");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Files.FileNames.Sanitize(asset.Name));
        using (var request = Request(asset.Url))
        using (var response = await http.SendAsync(request, Stage.Update, cancel))
        await using (var body = await response.Content.ReadAsStreamAsync(cancel))
        await using (var file = File.Create(path))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await ReadAsync(body, buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (asset.Size > 0)
                    progress?.Report((double)done / asset.Size);
            }
            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase))
            {
                await file.DisposeAsync();
                File.Delete(path);
                throw new StageException(Stage.Update, $"{asset.Name} doesn't match its SHA-256 in {ChecksumsName}; it was deleted.");
            }
        }
        return path;
    }

    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(1);

    /// <summary>The next bytes; a minute without any is a failure, as is a dropped connection.</summary>
    private static async Task<int> ReadAsync(Stream body, byte[] buffer, CancellationToken cancel)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallTimeout);
        try
        {
            return await body.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new StageException(Stage.Update, $"The download stopped: no data for {StallTimeout.TotalSeconds:0} s.", isRetryable: true);
        }
        catch (IOException e)
        {
            throw new StageException(Stage.Update, $"The download dropped: {e.Message}", isRetryable: true, inner: e);
        }
    }

    /// <summary>"&lt;sha256&gt;  &lt;name&gt;" lines (sha256sum's format, "*" for binary mode too) as name → hash.</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space != 64)
                continue;
            sums[line[(space + 1)..].TrimStart(' ', '*')] = line[..space].ToLowerInvariant();
        }
        return sums;
    }

    /// <summary>"v0.2.0", "0.2.0" or "0.2.0+commit" → 0.2.0.</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        var bare = text.Trim().TrimStart('v', 'V');
        var plus = bare.IndexOfAny(['+', '-']);
        if (plus >= 0)
            bare = bare[..plus];
        if (Version.TryParse(bare, out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private async Task<Release?> GetAsync(string path, CancellationToken cancel)
    {
        GitHubRelease? release;
        try
        {
            release = JsonSerializer.Deserialize(await GetTextAsync(new Uri(_api, path), cancel), ReleasesJson.Default.GitHubRelease);
        }
        catch (StageException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null; // no release yet
        }
        catch (JsonException e)
        {
            throw new StageException(Stage.Update, "GitHub's answer about the release could not be read.", inner: e);
        }
        if (release is null || release.Draft || release.TagName is null || !TryParseVersion(release.TagName, out var version))
            return null;
        // a file without a name or a usable address can't be fetched, so it isn't offered
        var assets = (release.Assets ?? [])
            .Where(a => a.Name is { Length: > 0 } && Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out _))
            .Select(a => new ReleaseAsset(a.Name!, new Uri(a.BrowserDownloadUrl!), a.Size));
        var page = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var html) ? html : ReleasesPage;
        return new Release(release.TagName, version, release.Body ?? "", page, [.. assets]);
    }

    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    private async Task<string> GetTextAsync(Uri url, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(AnswerTimeout);
        using var request = Request(url);
        try
        {
            using var response = await http.SendAsync(request, Stage.Update, timeout.Token);
            return await HttpFetcher.ReadTextAsync(response, MaxAnswerBytes, Stage.Update, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new StageException(Stage.Update, $"GitHub didn't answer within {AnswerTimeout.TotalSeconds:0} s.", isRetryable: true);
        }
    }

    private static HttpRequestMessage Request(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"{Product.Name}/{Product.Version}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    internal sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size);

    internal sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets);
}

/// <summary>Source-generated JSON for GitHub's release answers (AOT).</summary>
[JsonSerializable(typeof(Releases.GitHubRelease))]
internal sealed partial class ReleasesJson : JsonSerializerContext;
