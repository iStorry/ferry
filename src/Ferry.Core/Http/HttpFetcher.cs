using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ferry.Core.Http;

/// <summary>
/// The one HTTP client for everything Ferry fetches: pages, the API, the sign step and the files. Failures come
/// out as <see cref="StageException"/> for the step that was running, marked retryable or not.
/// </summary>
public sealed class HttpFetcher : IDisposable
{
    private const int ErrorSnippetChars = 200;
    private const int ErrorSnippetBytes = 16 << 10;
    private const int MaxRedirects = 10;
    private static readonly HashSet<string> HeadersForAnySite = new(StringComparer.OrdinalIgnoreCase) { "User-Agent", "Range", "Referer", "Accept" };
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleConnectionTimeout = TimeSpan.FromSeconds(90);

    // a connection is used no longer than this, so a CDN that moved an edge or changed a DNS answer is followed
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(10);
    private readonly HttpClient _client;
    private readonly HttpClient _files;
    private readonly PublicDns? _dns;

    /// <param name="connectTimeout">how long a server may take to accept a connection</param>
    /// <param name="publicDnsHosts">hosts whose address comes from <see cref="PublicDns"/>, not this machine's resolver</param>
    public HttpFetcher(TimeSpan? connectTimeout = null, IEnumerable<string>? publicDnsHosts = null)
    {
        _dns = publicDnsHosts?.ToList() is { Count: > 0 } hosts ? new PublicDns(hosts) : null;
        _client = Client(DecompressionMethods.All, connectTimeout, _dns);
        // a file's bytes as the server holds them: unpacked, a .tar.gz wouldn't be one, and ranges wouldn't add up
        _files = Client(DecompressionMethods.None, connectTimeout, _dns);
    }

    /// <summary>A connection as the handler would make it, to an address <paramref name="dns"/> gives for the host.</summary>
    private static async ValueTask<Stream> ConnectAsync(PublicDns dns, SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var (host, port) = (context.DnsEndPoint.Host, context.DnsEndPoint.Port);
        var addresses = await dns.AddressesAsync(host, cancel);
        // public DNS gives IPv4; the machine's answer may be either, and a dual-mode socket takes both
        var socket = addresses.All(address => address.AddressFamily == AddressFamily.InterNetwork)
            ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }
            : new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, cancel);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static HttpClient Client(DecompressionMethods decompression, TimeSpan? connectTimeout, PublicDns? dns) =>
        new(new SocketsHttpHandler
        {
            // only the hosts it names connect another way; the rest as the handler does by itself
            ConnectCallback = dns is null ? null : (context, cancel) =>
                dns.Handles(context.DnsEndPoint.Host) ? ConnectAsync(dns, context, cancel) : DefaultConnectAsync(context, cancel),
            ConnectTimeout = connectTimeout ?? DefaultConnectTimeout,
            PooledConnectionIdleTimeout = IdleConnectionTimeout,
            PooledConnectionLifetime = ConnectionLifetime,
            AutomaticDecompression = decompression,
            // cookies are what a site or the user gave for one file: a jar shared by every download would send a
            // Set-Cookie from one server to unrelated downloads of the same site, beside the headers set by hand.
            // SendAsync carries what a redirect sets to the next hops of that one request (RedirectCookies)
            UseCookies = false,
            // SendAsync follows them: the handler would carry cookies set by hand on to any site a redirect names
            AllowAutoRedirect = false,
            // a download that stops reading early (another connection took over the rest of its part) closes its
            // connection rather than reading on in the background: those bytes would be wasted, ignore the speed
            // limit, and hold a connection beyond the cap per host
            MaxResponseDrainSize = 0,
        })
        {
            // each step sets its own timeout (Retry); the client's would cut a long download short
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// <summary>Send <paramref name="request"/>, following redirects; only a successful response comes back.</summary>
    /// <param name="asFile">the answer is a file to keep, not something to read</param>
    /// <exception cref="StageException">the connection failed, or the server answered with an error status</exception>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Stage stage, CancellationToken cancel, bool asFile = false)
    {
        var current = request;
        var cookies = new RedirectCookies();
        for (var redirects = 0; ; redirects++)
        {
            var response = await SendOnceAsync(asFile ? _files : _client, current, stage, cancel);
            if (response.IsSuccessStatusCode)
                return response;
            if (!IsRedirect(response.StatusCode))
                throw await FailureAsync(response, current, stage, cancel);
            var status = response.StatusCode;
            Uri target;
            using (response)
            {
                target = RedirectTarget(response, current, stage);
                cookies.Note(response, current.RequestUri!);
            }
            if (redirects == MaxRedirects)
                throw new StageException(stage, $"{current.RequestUri?.Host} redirected too many times.");
            current = Redirected(current, target, status);
            cookies.ApplyTo(current);
        }
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(HttpClient client, HttpRequestMessage request, Stage stage, CancellationToken cancel)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        }
        catch (HttpRequestException e)
        {
            throw new StageException(stage, $"Could not reach {request.RequestUri?.Host}: {e.Message}", isRetryable: true,
                                     inner: e);
        }
    }

    private static async Task<StageException> FailureAsync(HttpResponseMessage response, HttpRequestMessage request, Stage stage,
                                                           CancellationToken cancel)
    {
        using (response)
        {
            var snippet = await ReadSnippetAsync(response, cancel);
            return new StageException(stage,
                $"{request.RequestUri?.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}{snippet}.",
                IsRetryable(response.StatusCode), response.Headers.RetryAfter?.Delta, status: response.StatusCode);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Where a redirect leads.</summary>
    /// <exception cref="StageException">it says nowhere, or leads from https to http, which is never followed</exception>
    private static Uri RedirectTarget(HttpResponseMessage response, HttpRequestMessage request, Stage stage)
    {
        var from = request.RequestUri!;
        if (response.Headers.Location is not { } location)
            throw new StageException(stage, $"{from.Host} redirected without saying where to.");
        var target = new Uri(from, location);
        var allowed = target.Scheme == Uri.UriSchemeHttps || (target.Scheme == Uri.UriSchemeHttp && from.Scheme == Uri.UriSchemeHttp);
        return allowed ? target : throw new StageException(stage, $"{from.Host} redirected from https to http, which Ferry doesn't follow.");
    }

    /// <summary>
    /// The same request for where a redirect leads. A different site gets only what says nothing about the user: cookies
    /// and other headers may be logins for the site they were given for.
    /// </summary>
    private static HttpRequestMessage Redirected(HttpRequestMessage request, Uri target, HttpStatusCode status)
    {
        var keepsBody = status is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect || request.Method == HttpMethod.Head;
        var next = new HttpRequestMessage(keepsBody ? request.Method : HttpMethod.Get, target)
        {
            Content = keepsBody ? request.Content : null,
            Version = request.Version,
        };
        var sameSite = IsSameSite(request.RequestUri, target);
        foreach (var (name, values) in request.Headers)
        {
            if (sameSite || HeadersForAnySite.Contains(name))
                next.Headers.TryAddWithoutValidation(name, values);
        }
        return next;
    }

    /// <summary>Whether what a person gave for one of these links may go to the other: the same scheme and host.</summary>
    public static bool IsSameSite(Uri? one, Uri? other) =>
        Uri.Compare(one, other, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>The body as text, failing if it is longer than <paramref name="maxBytes"/>.</summary>
    public static async Task<string> ReadTextAsync(HttpResponseMessage response, long maxBytes, Stage stage,
                                                   CancellationToken cancel)
    {
        using var buffer = await ReadAllAsync(response, maxBytes, stage, cancel);
        return EncodingOf(response).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The whole answer, as <see cref="ReadTextAsync"/> reads it, as bytes.</summary>
    public static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, long maxBytes, Stage stage,
                                                    CancellationToken cancel)
    {
        using var buffer = await ReadAllAsync(response, maxBytes, stage, cancel);
        return buffer.ToArray();
    }

    private static async Task<MemoryStream> ReadAllAsync(HttpResponseMessage response, long maxBytes, Stage stage,
                                                         CancellationToken cancel)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw TooLarge(stage, maxBytes);
        var buffer = new MemoryStream();
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancel);
            var chunk = new byte[1 << 16];
            int read;
            while ((read = await body.ReadAsync(chunk, cancel)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw TooLarge(stage, maxBytes);
                buffer.Write(chunk, 0, read);
            }
            return buffer;
        }
        catch (IOException e)
        {
            buffer.Dispose();
            throw new StageException(stage, $"The connection dropped while reading the answer: {e.Message}", isRetryable: true, inner: e);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>The answer's charset; UTF-8 when it names none, or one .NET doesn't have.</summary>
    private static Encoding EncodingOf(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.CharSet is not { Length: > 0 } charset)
            return Encoding.UTF8;
        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>Worth trying again: the server was busy or broken for a moment, not refusing for good.</summary>
    public static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>What the handler does without a ConnectCallback: a dual-mode socket to the host by name.</summary>
    private static async ValueTask<Stream> DefaultConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancel);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _files.Dispose();
        _dns?.Dispose();
    }

    private static StageException TooLarge(Stage stage, long maxBytes) =>
        new(stage, $"The answer is larger than {maxBytes / 1024} KB, so it is not what Ferry expected.");

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        try
        {
            // one line, no control characters: it ends up in messages and on terminals
            await using var stream = await response.Content.ReadAsStreamAsync(cancel);
            var buffer = new byte[ErrorSnippetBytes]; // no more than this of an error page, however large it says it is
            var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancel);
            var text = string.Join(' ', Encoding.UTF8.GetString(buffer, 0, length)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => new string([.. word.Where(c => !char.IsControl(c))])));
            return text.Length == 0 ? "" : $" (\"{(text.Length > ErrorSnippetChars ? text[..ErrorSnippetChars] + "…" : text)}\")";
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return ""; // the status alone says what went wrong
        }
    }
}
