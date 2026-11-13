using System.Net;
using System.Net.Http.Headers;
using Ferry.Core.Files;
using Ferry.Core.Http;

namespace Ferry.Core.Sites;

/// <summary>Asks a link for its first byte: what the server says the file is, without downloading it.</summary>
public static class LinkProbe
{
    private const string WebPageType = "text/html";

    public static RetryPolicy AskRetry { get; } =
        new(MaxRetries: 2, Wait: TimeSpan.FromSeconds(1), Timeout: TimeSpan.FromSeconds(30), SlowTimeout: TimeSpan.FromSeconds(75));

    public static bool IsWebLink(Uri link) => link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp;

    /// <summary>The file at <paramref name="url"/>, whatever it is, a web page included.</summary>
    public static Task<ResolvedFile> ResolveAsync(HttpFetcher http, string userAgent, FileLink file, Uri url, RequestDetails details, CancellationToken cancel) =>
        Retry.RunAsync(Stage.Resolve, AskRetry, async tryCancel =>
        {
            try
            {
                using var response = await AskAsync(http, userAgent, file, url, details, tryCancel);
                return Read(response, file, url, userAgent);
            }
            catch (StageException e) when (e.Status == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // no first byte to give: the whole of it says its name, and its length if there is one
                using var response = await AskAsync(http, userAgent, file, url, details, tryCancel, firstByteOnly: false);
                var found = Read(response, file, url, userAgent);
                return found.Size is null && await IsEmptyAsync(response, tryCancel) ? found with { Size = 0 } : found;
            }
        }, cancel);

    /// <summary>The file at <paramref name="url"/>, where a web page can't be it.</summary>
    /// <param name="ifWebPage">what it means, for the user, when a web page answers</param>
    /// <exception cref="StageException">a step failed, or a web page answered</exception>
    public static async Task<ResolvedFile> ResolveFileAsync(HttpFetcher http, string userAgent, FileLink file, Uri url, RequestDetails details, string ifWebPage, CancellationToken cancel)
    {
        var found = await ResolveAsync(http, userAgent, file, url, details, cancel);
        return found.IsWebPage ? throw new StageException(Stage.Resolve, ifWebPage) : found;
    }

    /// <summary>One try: only a successful answer comes back. A login goes with it only on the link's own site.</summary>
    /// <param name="firstByteOnly">ask for the first byte alone; false asks for the whole file, for the caller to read</param>
    public static async Task<HttpResponseMessage> AskAsync(HttpFetcher http, string userAgent, FileLink file, Uri url, RequestDetails details, CancellationToken cancel,
                                                           bool firstByteOnly = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        if (firstByteOnly)
            request.Headers.Range = new RangeHeaderValue(0, 0);
        if (HttpFetcher.IsSameSite(url, file.Original))
            details.ApplyTo(request);
        return await http.SendAsync(request, Stage.Resolve, cancel, asFile: true);
    }

    public static bool IsWebPage(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, WebPageType, StringComparison.OrdinalIgnoreCase);

    /// <summary>What <paramref name="response"/> from <see cref="AskAsync"/> says the file at <paramref name="url"/> is.</summary>
    public static ResolvedFile Read(HttpResponseMessage response, FileLink file, Uri url, string userAgent)
    {
        var partial = response.StatusCode == HttpStatusCode.PartialContent;
        var size = partial ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;
        return new ResolvedFile(file, file.Key, NameOf(response, file), url, Referer: null, userAgent, ExpiresAt: null, IsWebPage(response), size)
        {
            Probe = new ProbeResult(partial, response.Headers.ETag?.Tag, response.Content.Headers.LastModified),
        };
    }

    private static async Task<bool> IsEmptyAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        return await body.ReadAsync(new byte[1], cancel) == 0;
    }

    /// <summary>The name the server gives; else the end of the link it ended up at, which a redirect may have made a real name.</summary>
    private static string NameOf(HttpResponseMessage response, FileLink file)
    {
        var given = response.Content.Headers.ContentDisposition is { } disposition
            ? (disposition.FileNameStar ?? disposition.FileName)?.Trim('"')
            : null;
        var landed = response.RequestMessage?.RequestUri is { } final ? FileLink.LastSegment(final) : "";
        return FileNames.Sanitize(given is { Length: > 0 } ? given : landed.Length > 0 ? landed : file.Label);
    }
}
