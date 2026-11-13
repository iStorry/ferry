using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Json;
using Ferry.Core.Sites;

namespace Ferry.Plugins.FileHosts;

/// <summary>What Gofile's API says is in a folder.</summary>
internal sealed record GofileEntry(string Id, string Name, Uri Url, long? Size);

/// <summary>A folder's name, and the files in it.</summary>
internal sealed record GofileFolder(string? Title, IReadOnlyList<GofileEntry> Files);

/// <summary>
/// Gofile's API, as its own site uses it: a guest account for this run of Ferry, and a token its site's script holds. Its
/// answer gives each file's address, which only answers with that guest account's cookie. This isn't a documented way in.
/// The account, the token and each folder read are kept, so a folder of many files is read once, not once per file.
/// </summary>
/// <param name="api">where its API is; tests stand in a server of their own</param>
/// <param name="site">where its site is, whose script holds the token</param>
internal sealed partial class GofileApi(HttpFetcher http, string userAgent, Uri api, Uri site) : IDisposable
{
    private const string Who = "Gofile";

    private static readonly TimeSpan FolderLifetime = TimeSpan.FromMinutes(1);
    private static readonly Uri Api = new("https://api.gofile.io/");
    private static readonly Uri Site = new("https://gofile.io/");

    private readonly Uri _accounts = new(api, "accounts");
    private readonly Uri _contents = new(api, "contents/");
    private readonly Uri _script = new(site, "dist/js/config.js");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset Until, GofileFolder Folder)> _folders = new(StringComparer.Ordinal);
    private string? _account;
    private string? _websiteToken;

    public GofileApi(HttpFetcher http, string userAgent) : this(http, userAgent, Api, Site) { }

    /// <summary>The guest account's token. A try that fails keeps nothing, so the next asks again.</summary>
    public async Task<string> AccountAsync(CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            return _account ??= await NewAccountAsync(cancel);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The folder as it is now.</summary>
    public Task<GofileFolder> ListFolderAsync(string folder, CancellationToken cancel) => ReadFolderAsync(folder, fresh: true, cancel);

    /// <summary>One file of a folder, from the folder as last read; read again when the file isn't in that, as one added since wouldn't be. Null when it still isn't.</summary>
    public async Task<GofileEntry?> FindFileAsync(string folder, string fileId, CancellationToken cancel)
    {
        var kept = await ReadFolderAsync(folder, fresh: false, cancel);
        if (kept.Files.FirstOrDefault(file => file.Id == fileId) is { } found)
            return found;
        var fresh = await ReadFolderAsync(folder, fresh: true, cancel);
        return fresh.Files.FirstOrDefault(file => file.Id == fileId);
    }

    private async Task<GofileFolder> ReadFolderAsync(string folder, bool fresh, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            if (!fresh && _folders.TryGetValue(folder, out var kept) && kept.Until > DateTimeOffset.UtcNow)
                return kept.Folder;
            var read = await FetchFolderAsync(folder, cancel);
            _folders[folder] = (DateTimeOffset.UtcNow + FolderLifetime, read);
            return read;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Under the gate: the account and the token are made here when there are none yet.</summary>
    private async Task<GofileFolder> FetchFolderAsync(string folder, CancellationToken cancel)
    {
        var account = _account ??= await NewAccountAsync(cancel);
        var pass = _websiteToken ??= await WebsiteTokenAsync(cancel);
        var login = new RequestDetails(Headers: new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = $"Bearer {account}", ["X-Website-Token"] = pass });
        try
        {
            return Read(await http.GetTextAsync(UrlQuery.With(new Uri(_contents, Uri.EscapeDataString(folder)), ("wt", pass), ("cache", "true")), userAgent, HttpAnswers.MaxApiBytes, Stage.Resolve, cancel, login));
        }
        catch (StageException e) when (e.Status is HttpStatusCode.Unauthorized)
        {
            // the guest account or the token has stopped working: the next try makes new ones
            (_account, _websiteToken) = (null, null);
            throw new StageException(Stage.Resolve, "Gofile no longer accepts Ferry's guest account; trying again with a new one.", isRetryable: true, inner: e, status: e.Status);
        }
    }

    private async Task<string> NewAccountAsync(CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _accounts);
        request.Headers.UserAgent.ParseAdd(userAgent);
        using var response = await http.SendAsync(request, Stage.Resolve, cancel);
        using var json = JsonAnswer.Parse(await HttpFetcher.ReadTextAsync(response, HttpAnswers.MaxApiBytes, Stage.Resolve, cancel), Who);
        return json.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("token", out var token) && token.GetString() is { Length: > 0 } value
            ? value
            : throw new StageException(Stage.Resolve, "Gofile didn't give a guest account. Its API may have changed.");
    }

    /// <summary>The token in its site's script.</summary>
    private async Task<string> WebsiteTokenAsync(CancellationToken cancel)
    {
        var script = await http.GetTextAsync(_script, userAgent, HttpAnswers.MaxPageBytes, Stage.Resolve, cancel);
        return WebsiteToken().Match(script) is { Success: true } found
            ? found.Groups["token"].Value
            : throw new StageException(Stage.Resolve, "Gofile's site has changed: the token Ferry needs for its API isn't in its script any more.");
    }

    public void Dispose() => _gate.Dispose();

    internal static GofileFolder Read(string text)
    {
        using var json = JsonAnswer.Parse(text, Who);
        if (!json.RootElement.TryGetProperty("data", out var data) || (json.RootElement.TryGetProperty("status", out var status) && status.GetString() != "ok"))
            throw new StageException(Stage.Resolve, $"Gofile refused: {(json.RootElement.TryGetProperty("status", out var why) ? why.GetString() : "no answer")}. The folder may be private or gone, or its API may have changed.");
        var files = new List<GofileEntry>();
        if (data.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Object)
        {
            foreach (var child in children.EnumerateObject())
            {
                if (child.Value.TryGetProperty("type", out var type) && type.GetString() == "file"
                    && child.Value.TryGetProperty("link", out var link) && Uri.TryCreate(link.GetString(), UriKind.Absolute, out var url))
                {
                    files.Add(new GofileEntry(child.Name, child.Value.TryGetProperty("name", out var name) ? name.GetString() ?? child.Name : child.Name, url,
                                              child.Value.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null));
                }
            }
        }
        return new GofileFolder(data.TryGetProperty("name", out var title) ? title.GetString() : null, files);
    }

    [GeneratedRegex("""wt\s*[:=]\s*["'](?<token>[A-Za-z0-9]+)["']""")]
    private static partial Regex WebsiteToken();
}

/// <summary>One file of a Gofile folder: "gofile.io/d/{folder}#{file}", which <see cref="GofileLister"/> makes from a link to the folder.</summary>
internal sealed partial class GofileSite(string userAgent, GofileApi api) : ISite
{
    public const string SiteId = "gofile";

    private static readonly HostSet Hosts = HostSet.Of("gofile.io");

    public string Id => SiteId;

    public string Name => "Gofile";

    public bool Claims(Uri link) => IsGofile(link) && FolderPage().IsMatch(link.AbsolutePath);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = null;
        reason = null;
        if (link.Fragment.Length <= 1)
        {
            reason = $"\"{link}\" is a folder link. Folders are added through {FileHostsPlugin.Plugin.Name}.";
            return false;
        }
        file = new FileLink(SiteId, link, link.Fragment[1..], "Gofile file");
        return true;
    }

    public async Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        var folder = FolderPage().Match(file.Original.AbsolutePath).Groups["id"].Value;
        var entry = await Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, tryCancel => api.FindFileAsync(folder, file.Key, tryCancel), cancel)
            ?? throw new StageException(Stage.Resolve, "This file isn't in its Gofile folder any more.");
        return new ResolvedFile(file, file.Key, FileNames.Sanitize(entry.Name), entry.Url, Referer: null, userAgent, ExpiresAt: null, IsWebPage: false, entry.Size,
                                Cookie: $"accountToken={await api.AccountAsync(cancel)}");
    }

    internal static bool IsGofile(Uri link) => Hosts.Claims(link);

    [GeneratedRegex("^/d/(?<id>[A-Za-z0-9-]+)/?$")]
    internal static partial Regex FolderPage();
}

/// <summary>A link to a Gofile folder, as a link to each file in it.</summary>
internal sealed class GofileLister(GofileApi api) : IFileLister
{
    public async Task<FileList?> TryListAsync(Uri link, RequestDetails details, CancellationToken cancel)
    {
        if (!GofileSite.IsGofile(link) || link.Fragment.Length > 1 || GofileSite.FolderPage().Match(link.AbsolutePath) is not { Success: true } folder)
            return null;
        var (title, files) = await Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, tryCancel => api.ListFolderAsync(folder.Groups["id"].Value, tryCancel), cancel);
        return files.Count > 0
            ? new FileList(title, [.. files.Select(file => new UriBuilder(link) { Fragment = file.Id }.Uri.ToString())])
            : throw new StageException(Stage.Link, "This Gofile folder has no files that can be read.");
    }
}
