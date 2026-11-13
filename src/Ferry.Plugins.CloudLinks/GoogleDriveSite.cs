using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Sites;
using Ferry.Plugins.Folders;

namespace Ferry.Plugins.CloudLinks;

/// <summary>
/// Google Drive's file links. A file too large to scan answers with a warning page, whose form holds what lets it through.
/// A Google document's export link ("/export?format=pdf") is a file in its own right, asked for as it is.
/// </summary>
public sealed partial class GoogleDriveSite(HttpFetcher http, string userAgent) : ISite
{
    public const string SiteId = "google-drive";

    private const string DocsHost = "docs.google.com";
    private const string DriveFilePath = "/uc";
    private const string NotGiven = "Google Drive sent a web page, not the file. The file may be private, and Ferry can't log in to Google for it, or too many people may have downloaded it today.";
    private const string DocumentNotGiven = "Google sent a web page, not the exported document. The document may be private, and Ferry can't log in to Google for it.";

    private static readonly HostSet Hosts = HostSet.Of("drive.google.com", "drive.usercontent.google.com", DocsHost);
    private static readonly Uri DownloadUrl = new("https://drive.usercontent.google.com/download");

    public string Id => SiteId;

    public string Name => "Google Drive";

    public bool Claims(Uri link) => Hosts.Claims(link);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = null;
        reason = null;
        var path = link.AbsolutePath;
        if (FolderPath().IsMatch(path))
        {
            reason = $"\"{link}\" is a folder link. Folders are added through {FoldersPlugin.Plugin.Name}.";
            return false;
        }
        if (IsExport(link))
        {
            file = new FileLink(SiteId, link, LinkKeys.WithoutFragment(link), "Google document");
            return true;
        }
        if (string.Equals(link.Host, DocsHost, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(DriveFilePath, StringComparison.Ordinal))
        {
            reason = $"\"{link}\" is a Google document, which isn't a file. Download it from Google as one, then add that.";
            return false;
        }
        if ((FilePath().Match(path) is { Success: true } match ? match.Groups["id"].Value : UrlQuery.Value(link, "id")) is not { Length: > 0 } id)
        {
            reason = $"\"{link}\" is not a link to a file on Google Drive.";
            return false;
        }
        file = new FileLink(SiteId, link, id, "Google Drive file");
        return true;
    }

    public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel) =>
        IsExport(file.Original)
            ? LinkProbe.ResolveFileAsync(http, userAgent, file, file.Original, details, DocumentNotGiven, cancel)
            : Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, async tryCancel =>
            {
                var url = UrlOf(file.Key);
                string? pass;
                using (var first = await LinkProbe.AskAsync(http, userAgent, file, url, details, tryCancel))
                {
                    if (!LinkProbe.IsWebPage(first))
                        return LinkProbe.Read(first, file, url, userAgent);
                    pass = WarningPass(await HttpFetcher.ReadTextAsync(first, HttpAnswers.MaxPageBytes, Stage.Resolve, tryCancel));
                }
                if (pass is null)
                    throw new StageException(Stage.Resolve, NotGiven);
                url = UrlQuery.With(url, ("uuid", pass));
                using var second = await LinkProbe.AskAsync(http, userAgent, file, url, details, tryCancel);
                return LinkProbe.IsWebPage(second) ? throw new StageException(Stage.Resolve, NotGiven) : LinkProbe.Read(second, file, url, userAgent);
            }, cancel);

    private static Uri UrlOf(string id) => UrlQuery.With(DownloadUrl, ("id", id), ("export", "download"), ("confirm", "t"));

    private static bool IsExport(Uri link) => string.Equals(link.Host, DocsHost, StringComparison.OrdinalIgnoreCase) && ExportPath().IsMatch(link.AbsolutePath);

    /// <summary>What the warning page's form sends to say the warning was seen; null on any other page.</summary>
    internal static string? WarningPass(string page) =>
        WarningPassField().Match(page) is { Success: true } match ? match.Groups["pass"].Value : null;

    [GeneratedRegex(@"/d/(?<id>[A-Za-z0-9_-]+)")]
    private static partial Regex FilePath();

    [GeneratedRegex(@"/folders/|/folderview")]
    private static partial Regex FolderPath();

    [GeneratedRegex(@"^/[a-z]+/d/[A-Za-z0-9_-]+/export(?:/|$)")]
    private static partial Regex ExportPath();

    [GeneratedRegex("""name="uuid"\s+value="(?<pass>[^"]+)"|value="(?<pass>[^"]+)"\s+name="uuid\b""")]
    private static partial Regex WarningPassField();
}
