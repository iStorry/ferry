using System.Text.RegularExpressions;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.CloudLinks;

/// <summary>Dropbox's share links: asked for with "dl=1", they answer with the file, or a shared folder as a zip.</summary>
internal static partial class DropboxSite
{
    public const string SiteId = "dropbox";

    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "dropbox.com", "www.dropbox.com" };

    public static RewriteSite Create(HttpFetcher http, string userAgent) => new(http, userAgent, SiteId, "Dropbox", Hosts, ShareLink(),
        (link, _) => UrlQuery.With(link, ("dl", "1")),
        "Dropbox sent a web page, not the file. The link may need a password, or may have been turned off.");

    [GeneratedRegex("^/(scl/fi|scl/fo|s|sh|l)/")]
    private static partial Regex ShareLink();
}
