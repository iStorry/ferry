using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.FileHosts;

/// <summary>MediaFire's file pages: the download button on the page holds a link that lasts a while, and a new one comes from the page.</summary>
public sealed partial class MediaFireSite(HttpFetcher http, string userAgent) : ISite
{
    public const string SiteId = "mediafire";

    private const string NotFound = "MediaFire's page has no download button. The file may have been removed, or the page may have changed.";

    private static readonly HostSet Hosts = HostSet.Of("mediafire.com");

    public string Id => SiteId;

    public string Name => "MediaFire";

    public bool Claims(Uri link) => Hosts.Claims(link) && FilePage().IsMatch(link.AbsolutePath);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        var page = FilePage().Match(link.AbsolutePath);
        file = new FileLink(SiteId, link, page.Groups["id"].Value, page.Groups["name"].Success ? Uri.UnescapeDataString(page.Groups["name"].Value) : "MediaFire file");
        reason = null;
        return true;
    }

    public async Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        var page = await HtmlLinks.ReadPageAsync(http, userAgent, file.Original, details, cancel);
        var url = DownloadUrl(page) ?? throw new StageException(Stage.Resolve, NotFound);
        return await LinkProbe.ResolveFileAsync(http, userAgent, file, url, details, NotFound, cancel);
    }

    /// <summary>Where the page's button leads; the page may hide the address in base64.</summary>
    internal static Uri? DownloadUrl(string page)
    {
        var text = ButtonLink().Match(page) is { Success: true } plain ? WebUtility.HtmlDecode(plain.Groups["url"].Value)
            : ButtonScrambled().Match(page) is { Success: true } scrambled ? Unscrambled(scrambled.Groups["url"].Value)
            : null;
        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;
    }

    private static string? Unscrambled(string text)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [GeneratedRegex("^/file(?:_premium)?/(?<id>[A-Za-z0-9]+)(?:/(?<name>[^/]+))?")]
    private static partial Regex FilePage();

    [GeneratedRegex("""id="downloadButton"[^>]*?href="(?<url>https://[^"]+)"|href="(?<url>https://[^"]+)"[^>]*?id="downloadButton\b""")]
    private static partial Regex ButtonLink();

    [GeneratedRegex("""id="downloadButton"[^>]*?data-scrambled-url="(?<url>[^"]+)"|data-scrambled-url="(?<url>[^"]+)"[^>]*?id="downloadButton\b""")]
    private static partial Regex ButtonScrambled();
}
