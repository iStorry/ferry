using System.Diagnostics.CodeAnalysis;
using Ferry.Core.Http;

namespace Ferry.Core.Sites;

/// <summary>Any plain web link: the link is the file, and the server says what it is called.</summary>
public sealed class DirectSite(HttpFetcher http, string userAgent) : ISite
{
    public const string SiteId = "direct";

    public string Id => SiteId;

    public string Name => "Plain link";

    public bool Claims(Uri link) => LinkProbe.IsWebLink(link);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        // the part after # never reaches the server
        file = new FileLink(SiteId, link, new UriBuilder(link) { Fragment = "" }.Uri.ToString());
        reason = null;
        return true;
    }

    public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel) =>
        LinkProbe.ResolveAsync(http, userAgent, file, file.Original, details, cancel);
}
