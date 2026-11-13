using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Ferry.Core.Browse;

public enum PageKind
{
    /// <summary>A list of files.</summary>
    Album,

    /// <summary>One file.</summary>
    File,
}

/// <summary>A page the Browser can open: an album or a file, on a site some plugin reads.</summary>
/// <param name="Original">the link as it was given</param>
/// <param name="Slug">what names the album or file on its site, whichever of the site's hosts the link was on</param>
public sealed record PageLink(Uri Original, PageKind Kind, string Slug);

/// <summary>
/// Which of a plugin's links are pages to browse, said as patterns so that the PC, which doesn't run the plugin, and the
/// server, which does, tell them apart the same way. Each is matched against a link's path and names the page in a group
/// called "slug".
/// </summary>
/// <param name="Album">the path of an album's page, such as <c>^/a/(?&lt;slug&gt;[A-Za-z0-9_-]+)/?$</c></param>
/// <param name="File">the path of a file's page</param>
public sealed record PagePatterns(string Album, string File)
{
    private const string SlugGroup = "slug";

    // a pattern comes from a package: this kind can't be written to take forever on a link made for it
    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    /// <summary>Why these can't be used, or null when they can.</summary>
    public string? Problem() => Problem(Album) ?? Problem(File);

    /// <summary>A link that is its plugin's (the caller has checked), as the page it is; false when it is neither kind.</summary>
    public bool TryRead(Uri link, [NotNullWhen(true)] out PageLink? page)
    {
        page = Read(link, Album, PageKind.Album) ?? Read(link, File, PageKind.File);
        return page is not null;
    }

    private static PageLink? Read(Uri link, string pattern, PageKind kind) =>
        Regex.Match(link.AbsolutePath, pattern, Options) is { Success: true } match ? new PageLink(link, kind, match.Groups[SlugGroup].Value) : null;

    private static string? Problem(string pattern)
    {
        try
        {
            return new Regex(pattern, Options).GetGroupNames().Contains(SlugGroup) ? null : $"Its pattern for pages, {pattern}, has no group called \"{SlugGroup}\".";
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return $"Its pattern for pages, {pattern}, can't be used: {e.Message}";
        }
    }
}
