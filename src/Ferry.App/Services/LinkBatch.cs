using Ferry.Core.Http;

namespace Ferry.App.Services;

/// <summary>Links that came together, with what came with them. Kept apart from any others, so a login only goes to the links it came with.</summary>
/// <param name="Lines">the links</param>
/// <param name="Details">what to send with their requests, when they came with a login (from the browser)</param>
/// <param name="Album">a folder of their own for them, when they are a folder's files</param>
public sealed record LinkBatch(IReadOnlyList<string> Lines, RequestDetails? Details = null, string? Album = null)
{
    /// <summary>Text as lines, each a link (or a comment, which the queue passes over).</summary>
    public static string[] LinesOf(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
