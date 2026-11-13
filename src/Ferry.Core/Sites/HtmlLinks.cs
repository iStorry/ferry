using System.Net;
using System.Text.RegularExpressions;
using Ferry.Core.Http;

namespace Ferry.Core.Sites;

/// <summary>Web pages as plugins need them: read with a limit, and the links and title in them.</summary>
public static partial class HtmlLinks
{
    /// <summary>The page at <paramref name="url"/>, as text.</summary>
    /// <exception cref="StageException">it couldn't be read</exception>
    public static Task<string> ReadPageAsync(HttpFetcher http, string userAgent, Uri url, RequestDetails details, CancellationToken cancel) =>
        Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, tryCancel => http.GetTextAsync(url, userAgent, HttpAnswers.MaxPageBytes, Stage.Resolve, tryCancel, details), cancel);

    /// <summary>Where the page's links lead, as written in it, decoded from HTML.</summary>
    public static IEnumerable<string> Hrefs(string page) =>
        Href().Matches(page).Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value));

    /// <summary>The page's title, decoded from HTML; null when it has none.</summary>
    public static string? Title(string page) =>
        TitleTag().Match(page) is { Success: true } match && WebUtility.HtmlDecode(match.Groups["title"].Value).Trim() is { Length: > 0 } title ? title : null;

    [GeneratedRegex("""<a\s[^>]*?href\s*=\s*(?:"(?<url>[^"]*)"|'(?<url>[^']*)'|(?<url>[^\s"'>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex("<title[^>]*>(?<title>[^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();
}
