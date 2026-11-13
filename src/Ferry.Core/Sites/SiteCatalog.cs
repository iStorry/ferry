using System.Diagnostics.CodeAnalysis;
using Ferry.Core.Http;

namespace Ferry.Core.Sites;

/// <summary>A site in the catalog, and whether it is in use.</summary>
/// <param name="IsOn">off, its links are still known as its own, so none is taken for a plain web page, but nothing of it runs</param>
public sealed record SiteEntry(ISite Site, bool IsOn = true);

/// <summary>The sites Ferry knows, in the order they are asked: the first to claim a link gets it.</summary>
/// <param name="where">where these sites download, for the word on a link none of them takes ("on the server"); here when null</param>
public sealed class SiteCatalog(IReadOnlyList<SiteEntry> sites, string? where = null)
{
    /// <summary>What every install has, any plain link, asked last so that <paramref name="plugins"/> get theirs first.</summary>
    /// <param name="where">where these sites download, for the word on a link none of them takes ("on the server"); here when null</param>
    public static SiteCatalog Standard(HttpFetcher http, string userAgent, IEnumerable<SiteEntry> plugins, string? where = null) =>
        new([.. plugins, new SiteEntry(new DirectSite(http, userAgent))], where);

    /// <summary><paramref name="text"/> as a link to one file to add; false, with the reason for the user, when it can't be.</summary>
    public bool TryParse(string text, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = null;
        if (!TryFindOwner(text, out var link, out var owner, out reason))
            return false;
        if (!owner.IsOn)
        {
            reason = $"\"{link}\" is a {owner.Site.Name} link, and {owner.Site.Name} is off. It can be turned on in Ferry's Plugins.";
            return false;
        }
        return owner.Site.TryParseFile(link, out file, out reason);
    }

    /// <summary>A saved link as the file it was, also while its site is off: a file doesn't leave the queue for that.</summary>
    public bool TryRead(string text, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = null;
        return TryFindOwner(text, out var link, out var owner, out reason) && owner.Site.TryParseFile(link, out file, out reason);
    }

    /// <summary>The site that is off whose link this is, or null when it is no such link.</summary>
    public ISite? OffSiteOf(string text) =>
        TryFindOwner(text, out _, out var owner, out _) && !owner.IsOn ? owner.Site : null;

    /// <summary>What people call the site with this id; null when it isn't one of these.</summary>
    public string? NameOf(string siteId) => sites.FirstOrDefault(entry => entry.Site.Id == siteId)?.Site.Name;

    /// <summary>Whether what this site's files need can't run now.</summary>
    public bool IsOff(string siteId) => sites.Any(entry => entry.Site.Id == siteId && !entry.IsOn);

    /// <exception cref="StageException">the link's site isn't here or is off, or a step failed</exception>
    public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        var owner = sites.FirstOrDefault(entry => entry.Site.Id == file.SiteId)
                    ?? throw new StageException(Stage.Link, $"{file.Original} needs {file.SiteId}, which isn't available.");
        return owner.IsOn
            ? owner.Site.ResolveAsync(file, details, cancel)
            : throw new StageException(Stage.Link, $"This needs {owner.Site.Name}, which is off. Turn it on in Plugins, then try again.");
    }

    private bool TryFindOwner(string text, [NotNullWhen(true)] out Uri? link, [NotNullWhen(true)] out SiteEntry? owner, [NotNullWhen(false)] out string? reason)
    {
        var trimmed = text.Trim();
        owner = null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out link))
        {
            reason = $"\"{trimmed}\" is not a link.";
            return false;
        }
        var uri = link;
        owner = sites.FirstOrDefault(entry => entry.Site.Claims(uri));
        reason = owner is null ? $"\"{trimmed}\" is not a link Ferry can download{(where is null ? "" : " " + where)}." : null;
        return owner is not null;
    }
}
