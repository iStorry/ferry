namespace Ferry.Core.Http;

/// <summary>
/// How much of an answer Ferry reads before deciding it isn't what it asked for, and the one way to fetch text. Every
/// site's API answers and pages are read through these, so a limit is a decision made once.
/// </summary>
public static class HttpAnswers
{
    /// <summary>An API's answer about a file or a folder of them.</summary>
    public const long MaxApiBytes = 8 << 20;

    /// <summary>A web page, a playlist, a manifest.</summary>
    public const long MaxPageBytes = 16 << 20;

    /// <summary>GET <paramref name="url"/> as text, following redirects, with <paramref name="details"/> when given.</summary>
    /// <param name="maxBytes">fail past this many bytes: see <see cref="MaxApiBytes"/> and <see cref="MaxPageBytes"/></param>
    /// <exception cref="StageException">it couldn't be fetched or read, or is too large</exception>
    public static async Task<string> GetTextAsync(this HttpFetcher http, Uri url, string userAgent, long maxBytes, Stage stage,
                                                  CancellationToken cancel, RequestDetails? details = null, Uri? referer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Referrer = referer;
        details?.ApplyTo(request);
        using var response = await http.SendAsync(request, stage, cancel);
        return await HttpFetcher.ReadTextAsync(response, maxBytes, stage, cancel);
    }
}
