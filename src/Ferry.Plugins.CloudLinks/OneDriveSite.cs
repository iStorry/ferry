using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.CloudLinks;

/// <summary>OneDrive's share links. On SharePoint, "download=1" asks for the file; a personal link goes through OneDrive's shares API.</summary>
public sealed class OneDriveSite(HttpFetcher http, string userAgent) : ISite
{
    public const string SiteId = "onedrive";

    private const string SharePointHosts = ".sharepoint.com";
    private const string SharePointSharePath = "/:";
    private const string SharePointFolderPath = "/:f:";
    private const string ShortFolderPath = "/f/";
    private const string NotGiven = "OneDrive wouldn't give the file without a login. Open the link in your browser and download it there, or copy the download's own link into Ferry.";
    private const string NotFound = "OneDrive has no such file. It may have been removed, or the link may be wrong.";

    private static readonly HostSet PersonalHosts = HostSet.Of("1drv.ms", "onedrive.live.com");
    private static readonly Uri SharesApi = new("https://api.onedrive.com/v1.0/shares/");

    public string Id => SiteId;

    public string Name => "OneDrive";

    public bool Claims(Uri link) =>
        PersonalHosts.Claims(link) || (LinkProbe.IsWebLink(link) && IsSharePoint(link) && link.AbsolutePath.StartsWith(SharePointSharePath, StringComparison.Ordinal));

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        if (link.AbsolutePath.StartsWith(IsSharePoint(link) ? SharePointFolderPath : ShortFolderPath, StringComparison.Ordinal))
        {
            file = null;
            reason = $"\"{link}\" is a folder link. Ferry can't list OneDrive folders: add each file's own link.";
            return false;
        }
        file = new FileLink(SiteId, link, LinkKeys.WithoutFragment(link), "OneDrive file");
        reason = null;
        return true;
    }

    public async Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        var url = IsSharePoint(file.Original) ? UrlQuery.With(file.Original, ("download", "1")) : SharesUrl(file.Original);
        try
        {
            return await LinkProbe.ResolveFileAsync(http, userAgent, file, url, details, NotGiven, cancel);
        }
        catch (StageException e) when (e.Status is HttpStatusCode.NotFound)
        {
            throw new StageException(Stage.Resolve, NotFound, inner: e, status: e.Status);
        }
        catch (StageException e) when (e.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new StageException(Stage.Resolve, NotGiven, inner: e, status: e.Status);
        }
    }

    private static bool IsSharePoint(Uri link) => link.Host.EndsWith(SharePointHosts, StringComparison.OrdinalIgnoreCase);

    /// <summary>The link itself, encoded as the API's documentation says, names what is shared.</summary>
    internal static Uri SharesUrl(Uri link) =>
        new(SharesApi, $"u!{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(LinkKeys.WithoutFragment(link)))}/root/content");
}
