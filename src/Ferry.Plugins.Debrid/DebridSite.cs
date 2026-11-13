using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Json;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Debrid;

/// <summary>A link on a listed host, as the debrid service's direct link to the file. The key goes to the service only.</summary>
public sealed class DebridSite(HttpFetcher http, string userAgent, string service, string apiKey, IReadOnlyList<string> hosts) : ISite
{
    public const string SiteId = "debrid";

    private const string Who = "The debrid service";

    private static readonly Uri RealDebridUrl = new("https://api.real-debrid.com/rest/1.0/unrestrict/link");
    private static readonly Uri AllDebridUrl = new("https://api.alldebrid.com/v4/link/unlock");

    private readonly HostSet _hosts = HostSet.Under([.. hosts]);

    public string Id => SiteId;

    public string Name => service == DebridPlugin.AllDebrid ? "AllDebrid" : "Real-Debrid";

    public bool Claims(Uri link) => _hosts.Claims(link);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = new FileLink(SiteId, link, LinkKeys.WithoutFragment(link));
        reason = null;
        return true;
    }

    public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel) =>
        Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, async tryCancel =>
        {
            var answer = await AskAsync(file.Original, tryCancel);
            var (url, name, size) = service == DebridPlugin.AllDebrid ? ReadAllDebrid(answer) : ReadRealDebrid(answer);
            return new ResolvedFile(file, file.Key, FileNames.Sanitize(name ?? file.Label), url, Referer: null, userAgent, ExpiresAt: null, IsWebPage: false, size);
        }, cancel);

    private async Task<string> AskAsync(Uri link, CancellationToken cancel)
    {
        using var request = service == DebridPlugin.AllDebrid
            ? new HttpRequestMessage(HttpMethod.Get, UrlQuery.With(AllDebridUrl, ("agent", Product.Name), ("link", link.ToString())))
            : new HttpRequestMessage(HttpMethod.Post, RealDebridUrl)
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("link", link.ToString())]),
            };
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await http.SendAsync(request, Stage.Resolve, cancel);
            return await HttpFetcher.ReadTextAsync(response, HttpAnswers.MaxApiBytes, Stage.Resolve, cancel);
        }
        catch (StageException e) when (e.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new StageException(Stage.Resolve, $"{Name} refused the API key, or the account has run out. Check it in Debrid's options.", inner: e, status: e.Status);
        }
    }

    internal static (Uri Url, string? Name, long? Size) ReadRealDebrid(string json)
    {
        using var answer = JsonAnswer.Parse(json, Who);
        return Read(answer.RootElement, "download");
    }

    internal static (Uri Url, string? Name, long? Size) ReadAllDebrid(string json)
    {
        using var answer = JsonAnswer.Parse(json, Who);
        if (answer.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
            throw new StageException(Stage.Resolve, $"AllDebrid: {message.GetString()}");
        return answer.RootElement.TryGetProperty("data", out var data) ? Read(data, "link") : throw NoLink();
    }

    private static (Uri, string?, long?) Read(JsonElement file, string link) =>
        file.TryGetProperty(link, out var text) && Uri.TryCreate(text.GetString(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? (url,
               file.TryGetProperty("filename", out var name) ? name.GetString() : null,
               file.TryGetProperty("filesize", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null)
            : throw NoLink();

    private static StageException NoLink() => new(Stage.Resolve, "The debrid service gave no link for this file. It may not support this host right now.");
}
