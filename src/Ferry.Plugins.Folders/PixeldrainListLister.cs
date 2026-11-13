using System.Text.Json;
using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Json;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Folders;

/// <summary>A Pixeldrain list ("/l/{id}"), from its API. Its files are links File Hosts takes.</summary>
public sealed partial class PixeldrainListLister(HttpFetcher http, string userAgent) : IFileLister
{
    private const string Who = "Pixeldrain";

    private static readonly HostSet Hosts = HostSet.Of("pixeldrain.com");

    public async Task<FileList?> TryListAsync(Uri link, RequestDetails details, CancellationToken cancel)
    {
        if (!Hosts.Claims(link) || ListPath().Match(link.AbsolutePath) is not { Success: true } list)
            return null;
        var api = new Uri(link, $"/api/list/{list.Groups["id"].Value}");
        return Read(await Retry.RunAsync(Stage.Link, LinkProbe.AskRetry, tryCancel => http.GetTextAsync(api, userAgent, HttpAnswers.MaxApiBytes, Stage.Link, tryCancel), cancel), link);
    }

    internal static FileList Read(string json, Uri link)
    {
        using var answer = JsonAnswer.Parse(json, Who, Stage.Link);
        var title = answer.RootElement.TryGetProperty("title", out var name) ? name.GetString() : null;
        var files = answer.RootElement.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(file => file.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>()
                  .Select(id => new Uri(link, $"/u/{Uri.EscapeDataString(id)}").ToString()).ToList()
            : [];
        return files.Count > 0 ? new FileList(title, files) : throw new StageException(Stage.Link, "This Pixeldrain list has no files.");
    }

    [GeneratedRegex("^/l/(?<id>[A-Za-z0-9]+)/?$")]
    private static partial Regex ListPath();
}
