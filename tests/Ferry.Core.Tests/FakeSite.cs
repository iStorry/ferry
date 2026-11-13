using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Json;
using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Core.Tests;

/// <summary>
/// A local site of the kind that needs more than a plain link (HttpListener on loopback): a page per file, an API that says
/// where the file is stored, a service that signs links to it for a while, and the CDN (<see cref="CdnFile"/>), with failures
/// to order. <see cref="FakeFileSite"/> is the site that reads it. Tests never contact a real site.
/// </summary>
internal sealed class FakeSite : IDisposable
{
    private static readonly TimeSpan SignLifetime = TimeSpan.FromHours(1);
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _disposed = new();
    private readonly ConcurrentDictionary<string, bool> _tokens = new();
    private int _tokensIssued;
    private int _cdnActive, _cdnMostActive;
    private readonly Task _serving;

    public FakeSite()
    {
        (_listener, Url) = TestListener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public Uri Url { get; }

    /// <summary>File pages by slug.</summary>
    public ConcurrentDictionary<string, string> Pages { get; } = new();

    /// <summary>The API's JSON answer by file id.</summary>
    public ConcurrentDictionary<string, string> Api { get; } = new();

    /// <summary>The sign service's JSON answer by storage path.</summary>
    public ConcurrentDictionary<string, string> Signs { get; } = new();

    /// <summary>Files on the CDN by name, at "/cdn/{name}"; signing their path issues a new token each time.</summary>
    public ConcurrentDictionary<string, CdnFile> Files { get; } = new();

    /// <summary>Any other answer by path ("/repos/.../releases/latest"): its type and bytes, served as they are.</summary>
    public ConcurrentDictionary<string, (string Type, byte[] Body)> Static { get; } = new();

    /// <summary>The CDN answers 403 to every token, new ones too.</summary>
    public bool RefuseAllTokens { get; set; }

    /// <summary>File pages answer 403 unless the request's Cookie header has this in it: a login.</summary>
    public string? RequiredCookie { get; set; }

    public int TokensIssued => Volatile.Read(ref _tokensIssued);

    /// <summary>The most CDN requests that were being answered at the same time, over all files.</summary>
    public int CdnMostActive => Volatile.Read(ref _cdnMostActive);

    /// <summary>Statuses to answer with, in order, before serving a path normally ("/f/abc", "/api", "/sign").</summary>
    public ConcurrentDictionary<string, ConcurrentQueue<HttpStatusCode>> Failures { get; } = new();

    /// <summary>Every request served, as "METHOD /path?query", with its headers.</summary>
    public ConcurrentQueue<(string Request, System.Collections.Specialized.NameValueCollection Headers)> Requests { get; } = new();

    public FakeSiteOptions Options(int retries = 2) => new(Url, Fast(retries), Fast(retries));

    /// <summary>A file page for <paramref name="slug"/> whose API answer leads to <paramref name="data"/> on the CDN.</summary>
    public CdnFile AddFile(string slug, string id, string name, byte[] data)
    {
        Pages[slug] = $$"""{"id":"{{id}}","name":"{{name}}"}""";
        Api[id] = $$"""{"store":"{{Url}}","path":"/cdn/{{name}}","name":"{{name}}"}""";
        return Files[name] = new CdnFile(data);
    }

    /// <summary>Every token issued so far stops working.</summary>
    public void RevokeTokens()
    {
        foreach (var token in _tokens.Keys)
            _tokens[token] = false;
    }

    public void Fail(string path, params HttpStatusCode[] statuses) =>
        Failures[path] = new ConcurrentQueue<HttpStatusCode>(statuses);

    /// <summary>Paths ("/f/abc") that are never answered: the request waits until it gives up.</summary>
    public ConcurrentDictionary<string, bool> Hangs { get; } = new();

    public int Count(string pathPrefix) => Requests.Count(r => r.Request.Split(' ')[1].StartsWith(pathPrefix, StringComparison.Ordinal));

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (HttpListenerException)
        {
            // macOS's managed listener can throw closing while a request still hangs (Hangs); it is closed either way
        }
        _disposed.Cancel(); // the hanging requests end now, against a listener already closed
        _disposed.Dispose();
    }

    private static RetryPolicy Fast(int retries) =>
        new(retries, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => Respond(context));
        }
    }

    private async Task Respond(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url!.AbsolutePath;
        Requests.Enqueue(($"{request.HttpMethod} {request.Url.PathAndQuery}", request.Headers));
        if (Hangs.ContainsKey(path))
        {
            try
            {
                await Task.Delay(Timeout.Infinite, _disposed.Token);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
            try
            {
                context.Response.Abort();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                // the listener closed first: nothing left to abort
            }
            return;
        }
        if (path.StartsWith("/cdn/", StringComparison.Ordinal) && Files.TryGetValue(Uri.UnescapeDataString(path[5..]), out var file)
            && !Failures.ContainsKey(path))
        {
            var active = Interlocked.Increment(ref _cdnActive);
            for (var most = _cdnMostActive; active > most; most = _cdnMostActive)
                Interlocked.CompareExchange(ref _cdnMostActive, active, most);
            await file.ServeAsync(context, IsValid(request.QueryString["token"]), RevokeTokens,
                                  sent: () => Interlocked.Decrement(ref _cdnActive));
            return;
        }
        using var response = context.Response;
        if (Static.TryGetValue(path, out var fixedAnswer))
        {
            response.StatusCode = 200;
            response.ContentType = fixedAnswer.Type;
            response.ContentLength64 = fixedAnswer.Body.Length;
            await response.OutputStream.WriteAsync(fixedAnswer.Body);
            return;
        }
        if (Failures.TryGetValue(path, out var failures) && failures.TryDequeue(out var failure))
        {
            await Write(response, failure, "text/plain", "busy");
            return;
        }
        var (status, type, body) = (request.HttpMethod, path) switch
        {
            ("GET", _) when path.StartsWith("/f/", StringComparison.Ordinal) && RequiredCookie is { } login
                            && !(request.Headers["Cookie"] ?? "").Contains(login, StringComparison.Ordinal) =>
                (HttpStatusCode.Forbidden, "text/plain", "log in first"),
            ("GET", _) when path.StartsWith("/f/", StringComparison.Ordinal) && Pages.TryGetValue(path[3..], out var page) =>
                (HttpStatusCode.OK, "application/json", page),
            ("POST", "/api") when Api.TryGetValue(await ReadId(request), out var answer) => (HttpStatusCode.OK, "application/json", answer),
            ("GET", "/sign") when Signs.TryGetValue(request.QueryString["path"] ?? "", out var sign) => (HttpStatusCode.OK, "application/json", sign),
            ("GET", "/sign") when request.QueryString["path"] is { } cdnPath && cdnPath.StartsWith("/cdn/", StringComparison.Ordinal) =>
                (HttpStatusCode.OK, "application/json", IssueToken()),
            _ => (HttpStatusCode.NotFound, "text/plain", "not found"),
        };
        await Write(response, status, type, body);
    }

    private string IssueToken()
    {
        var token = $"tok{Interlocked.Increment(ref _tokensIssued)}";
        _tokens[token] = true;
        return $$"""{"token":"{{token}}","ex":{{DateTimeOffset.UtcNow.Add(SignLifetime).ToUnixTimeSeconds()}}}""";
    }

    private bool IsValid(string? token) =>
        !RefuseAllTokens && token is not null && _tokens.TryGetValue(token, out var valid) && valid;

    private static async Task<string> ReadId(HttpListenerRequest request)
    {
        using var document = await JsonDocument.ParseAsync(request.InputStream);
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
    }

    private static async Task Write(HttpListenerResponse response, HttpStatusCode status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)status;
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

}

/// <summary>Where <see cref="FakeSite"/> is and how hard to try.</summary>
public sealed record FakeSiteOptions(Uri BaseUrl, RetryPolicy PageRetry, RetryPolicy ApiRetry)
{
    public Uri ApiUrl => new(BaseUrl, "api");

    public Uri SignUrl => new(BaseUrl, "sign");
}

/// <summary>
/// The site that reads <see cref="FakeSite"/>, in three steps as such sites take: the file's page gives its id, the API says
/// where it is stored, and the sign service gives a link to it that works for a while. Links are
/// "https://files.test/f/{slug}", on either of two hosts, and the page is always fetched from the fake.
/// </summary>
public sealed partial class FakeFileSite(HttpFetcher http, FakeSiteOptions options, string userAgent) : ISite
{
    public const string SiteId = "fake";
    public const string Host = "files.test";
    public const string Mirror = "mirror.test";
    public static readonly Uri RefererBase = new("https://referer.test/file/");

    public string Id => SiteId;

    public string Name => "Fake";

    public bool Claims(Uri link) => LinkProbe.IsWebLink(link) && link.Host is Host or Mirror;

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        var match = FilePath().Match(link.AbsolutePath);
        file = match.Success ? new FileLink(SiteId, link, match.Groups["slug"].Value) : null;
        reason = match.Success ? null : $"\"{link}\" is not a Fake file link (expected {link.Host}/f/...).";
        return match.Success;
    }

    /// <param name="details">go with the file page only: the API and the sign service are other sites</param>
    public async Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        var page = await Retry.RunAsync(Stage.Resolve, options.PageRetry, async tryCancel =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.BaseUrl, "f/" + file.Key));
            request.Headers.UserAgent.ParseAdd(userAgent);
            details.ApplyTo(request);
            using var response = await http.SendAsync(request, Stage.Resolve, tryCancel);
            return await HttpFetcher.ReadTextAsync(response, HttpAnswers.MaxApiBytes, Stage.Resolve, tryCancel);
        }, cancel);
        var id = Text(page, "id", "The page") ?? throw new StageException(Stage.Resolve, "The page has no file on it (no file id). The link may be wrong or the file removed.");
        var stored = await Retry.RunAsync(Stage.Resolve, options.ApiRetry, async tryCancel =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl) { Content = new StringContent($$"""{"id":"{{id}}"}""", Encoding.UTF8, "application/json") };
            using var response = await http.SendAsync(request, Stage.Resolve, tryCancel);
            return await HttpFetcher.ReadTextAsync(response, HttpAnswers.MaxApiBytes, Stage.Resolve, tryCancel);
        }, cancel);
        var path = Text(stored, "path", "The API") ?? throw new StageException(Stage.Resolve, "The API did not say where the file is.");
        var url = new Uri(new Uri(Text(stored, "store", "The API")!), path);
        var signed = await Retry.RunAsync(Stage.Resolve, options.ApiRetry, async tryCancel =>
            await http.GetTextAsync(UrlQuery.With(options.SignUrl, ("path", path)), userAgent, HttpAnswers.MaxApiBytes, Stage.Resolve, tryCancel), cancel);
        using var sign = JsonAnswer.Parse(signed, "The sign service");
        if (Text(signed, "token", "The sign service") is not { } token || !sign.RootElement.TryGetProperty("ex", out var ex))
            throw new StageException(Stage.Resolve, "The sign service gave no token or expiry for the file.");
        var name = FileNames.Sanitize(Text(stored, "name", "The API") ?? Text(page, "name", "The page") ?? file.Key);
        return new ResolvedFile(file, id, name, UrlQuery.With(url, ("token", token), ("n", name)), new Uri(RefererBase, Uri.EscapeDataString(id)), userAgent,
                                DateTimeOffset.FromUnixTimeSeconds(ex.GetInt64()), IsWebPage: false, Size: null);
    }

    private static string? Text(string json, string field, string who)
    {
        using var answer = JsonAnswer.Parse(json, who);
        return answer.RootElement.ValueKind == JsonValueKind.Object && answer.RootElement.TryGetProperty(field, out var value)
               && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
    }

    [GeneratedRegex(@"^/f/(?<slug>[A-Za-z0-9_-]+)/?$")]
    private static partial Regex FilePath();
}

/// <summary>
/// <see cref="FakeFileSite"/> as a plugin, so that an agent started by a test downloads from the fake as it would from any
/// site: where the fake is comes with the settings, as a real site's address would.
/// </summary>
public static class FakeSitePlugin
{
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_FAKE", SettingType.Boolean, "true", "The tests' site.") { Title = "Fake" };

    public static readonly SettingKey Url = new("FERRY_FAKE_URL", SettingType.Url, "http://127.0.0.1:9/", "Where the fake site is.");

    public static readonly SettingKey PageTimeout = new("FERRY_FAKE_PAGE_TIMEOUT", SettingType.Duration, "5s", "How long the file page may take on the first try.");

    public static readonly SettingKey PageSlowTimeout = new("FERRY_FAKE_PAGE_SLOW_TIMEOUT", SettingType.Duration, "5s", "How long the file page may take on the last try.");

    public static SitePlugin Plugin { get; } = new("Fake", Enabled)
    {
        SiteIds = [FakeFileSite.SiteId],
        Sites = context => [new FakeFileSite(context.Http, Options(context.Settings), context.UserAgent)],
        Options = [Url, PageTimeout, PageSlowTimeout],
    };

    /// <summary>Two tries more for a page and three for the API, a millisecond apart.</summary>
    public static FakeSiteOptions Options(FerrySettings settings) =>
        new(new Uri(settings.Text(Url)),
            new RetryPolicy(2, TimeSpan.FromMilliseconds(1), settings.Duration(PageTimeout), settings.Duration(PageSlowTimeout)),
            new RetryPolicy(3, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
}
