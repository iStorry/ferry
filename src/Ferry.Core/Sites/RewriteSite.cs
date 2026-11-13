using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Ferry.Core.Http;

namespace Ferry.Core.Sites;

/// <summary>A site whose page for a file only needs its link rewritten to reach the file. It claims no other link on its hosts, which stay plain links.</summary>
/// <param name="fileLink">the paths of the links it claims</param>
/// <param name="fileUrl">where the bytes are, from a claimed link and how <paramref name="fileLink"/> matched it</param>
/// <param name="ifWebPage">what it means, for the user, when a web page answers instead</param>
/// <param name="hint">what to call the file until its name is known, when the end of its link won't do</param>
public sealed class RewriteSite(HttpFetcher http, string userAgent, string id, string name, IReadOnlySet<string> hosts, Regex fileLink,
                                Func<Uri, Match, Uri> fileUrl, string ifWebPage, Func<Match, string>? hint = null) : ISite
{
    public string Id => id;

    public string Name => name;

    public bool Claims(Uri link) => LinkProbe.IsWebLink(link) && hosts.Contains(link.Host) && fileLink.IsMatch(link.AbsolutePath);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = new FileLink(id, link, link.AbsolutePath, hint?.Invoke(fileLink.Match(link.AbsolutePath)));
        reason = null;
        return true;
    }

    public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel) =>
        LinkProbe.ResolveFileAsync(http, userAgent, file, fileUrl(file.Original, fileLink.Match(file.Original.AbsolutePath)), details, ifWebPage, cancel);
}
