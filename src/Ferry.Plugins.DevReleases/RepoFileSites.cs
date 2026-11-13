using System.Text.RegularExpressions;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.DevReleases;

/// <summary>The sites where a page about a file has a link to the file itself that only differs in its path.</summary>
internal static partial class RepoFileSites
{
    public const string GitLabId = "gitlab";
    public const string HuggingFaceId = "hugging-face";
    public const string SourceForgeId = "sourceforge";

    private const string LatestFile = "latest";

    private static readonly HashSet<string> GitLabHosts = new(StringComparer.OrdinalIgnoreCase) { "gitlab.com", "www.gitlab.com" };
    private static readonly HashSet<string> HuggingFaceHosts = new(StringComparer.OrdinalIgnoreCase) { "huggingface.co", "www.huggingface.co", "hf.co" };
    private static readonly HashSet<string> SourceForgeHosts = new(StringComparer.OrdinalIgnoreCase) { "sourceforge.net", "www.sourceforge.net" };
    private static readonly Uri SourceForgeFiles = new("https://downloads.sourceforge.net/project/");

    public static RewriteSite GitLab(HttpFetcher http, string userAgent) => new(http, userAgent, GitLabId, "GitLab", GitLabHosts, GitLabFilePage(),
        (link, match) => UrlQuery.With(new Uri(link, $"/{match.Groups["project"].Value}/-/raw/{match.Groups["file"].Value}"), ("inline", "false")),
        "GitLab sent a web page, not the file. The project may be private, or the file may have moved.");

    public static RewriteSite HuggingFace(HttpFetcher http, string userAgent) => new(http, userAgent, HuggingFaceId, "Hugging Face", HuggingFaceHosts, HuggingFaceFilePage(),
        (link, match) => UrlQuery.With(new Uri(link, $"/{match.Groups["repo"].Value}/resolve/{match.Groups["file"].Value}"), ("download", "true")),
        "Hugging Face sent a web page, not the file. The model may ask you to log in or accept its terms first.");

    /// <summary>Its mirrors only answer browsers with a page, so the file is asked for from its download host. The latest file has no link there, and may get that page.</summary>
    public static RewriteSite SourceForge(HttpFetcher http, string userAgent) => new(http, userAgent, SourceForgeId, "SourceForge", SourceForgeHosts, SourceForgeFilePage(),
        (link, match) => match.Groups["file"].Value == LatestFile
            ? link
            : new Uri(SourceForgeFiles, $"{match.Groups["project"].Value}/{match.Groups["file"].Value}"),
        "SourceForge sent a web page, not the file. Open the project's Files page and copy the link of the file you want.",
        match => Uri.UnescapeDataString(match.Groups["file"].Value.Split('/')[^1]));

    [GeneratedRegex("^/(?<project>.+?)/-/blob/(?<file>.+)$")]
    private static partial Regex GitLabFilePage();

    [GeneratedRegex("^/(?<repo>.+?)/blob/(?<file>.+)$")]
    private static partial Regex HuggingFaceFilePage();

    [GeneratedRegex("^/projects/(?<project>[^/]+)/files/(?<file>.+)/download/?$")]
    private static partial Regex SourceForgeFilePage();
}
