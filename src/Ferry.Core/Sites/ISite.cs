using System.Diagnostics.CodeAnalysis;
using Ferry.Core.Http;

namespace Ferry.Core.Sites;

/// <summary>A link to one file, on the site that claimed it.</summary>
/// <param name="Original">the link as it was given</param>
/// <param name="Key">identifies the file on its site, however the link was written</param>
/// <param name="Hint">what the link says the file is called, when its path doesn't</param>
public sealed record FileLink(string SiteId, Uri Original, string Key, string? Hint = null)
{
    /// <summary>What to call the file before its real name is known.</summary>
    public string Label => Hint ?? (LastSegment(Original) is { Length: > 0 } segment ? segment : Original.Host);

    /// <summary>The last part of a link's path, decoded; "" when it has none.</summary>
    public static string LastSegment(Uri link) => link.Segments is [.., var last] ? Uri.UnescapeDataString(last.TrimEnd('/')) : "";
}

/// <summary>Everything needed to download a file.</summary>
/// <param name="FileId">the file itself, whatever link leads to it: a new link to another file is an error</param>
/// <param name="Name">the file name to save as, already safe on every OS</param>
/// <param name="Url">where the bytes are</param>
/// <param name="Referer">the referer the server expects, if any</param>
/// <param name="ExpiresAt">when <paramref name="Url"/> stops working, if it does; resolve again after that</param>
/// <param name="Size">the file's size in bytes, when the site knows it before downloading</param>
/// <param name="IsWebPage">the link really is a web page; otherwise a web page in place of the file means the file is gone</param>
/// <param name="Transport">what brings the bytes over, when <paramref name="Url"/> isn't a plain web download</param>
/// <param name="Cookie">what the site itself says to send with <paramref name="Url"/>, unlike what the user gave, which only goes to the link's own site</param>
public sealed record ResolvedFile(FileLink Link, string FileId, string Name, Uri Url, Uri? Referer, string UserAgent,
                                  DateTimeOffset? ExpiresAt, bool IsWebPage, long? Size, IFileTransport? Transport = null, string? Cookie = null)
{
    /// <summary>What the server said when <see cref="LinkProbe"/> asked <see cref="Url"/> for its first byte, so the download needn't ask again.</summary>
    public ProbeResult? Probe { get; init; }
}

/// <summary>What a server says about a file when asked for its first byte, besides its size.</summary>
/// <param name="Ranges">it sends parts of the file (206 to a Range request), so several connections can share it</param>
/// <param name="ETag">the server's version of the file, if it gives one</param>
/// <param name="LastModified">when the file last changed, if the server says</param>
public sealed record ProbeResult(bool Ranges, string? ETag, DateTimeOffset? LastModified);

/// <summary>Where files can come from: what makes a link its own, and how to get from that link to the bytes.</summary>
public interface ISite
{
    string Id { get; }

    /// <summary>What people call it.</summary>
    string Name { get; }

    /// <summary>Whether this link is on this site, whatever it leads to.</summary>
    bool Claims(Uri link);

    /// <summary>A link this site claims, as a file; false, with the reason for the user, when it isn't one.</summary>
    bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason);

    /// <param name="details">what the user gave for this file, to send with whatever is asked of the site</param>
    /// <exception cref="StageException">a step failed</exception>
    Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel);
}
