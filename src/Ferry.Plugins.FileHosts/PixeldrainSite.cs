using System.Text.RegularExpressions;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.FileHosts;

/// <summary>Pixeldrain's file pages ("/u/{id}"): its API hands over the file by the same id.</summary>
internal static partial class PixeldrainSite
{
    public const string SiteId = "pixeldrain";

    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "pixeldrain.com", "www.pixeldrain.com" };

    public static RewriteSite Create(HttpFetcher http, string userAgent) => new(http, userAgent, SiteId, "Pixeldrain", Hosts, FilePage(),
        (link, match) => new Uri(link, $"/api/file/{match.Groups["id"].Value}?download"),
        "Pixeldrain sent a web page, not the file. It may have been removed.");

    [GeneratedRegex("^/u/(?<id>[A-Za-z0-9]+)/?$")]
    private static partial Regex FilePage();
}
