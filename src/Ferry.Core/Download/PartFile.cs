using System.Text.Json;
using Ferry.Core.Files;
using Ferry.Core.Json;

namespace Ferry.Core.Download;

/// <summary>What an unfinished download has done so far, saved beside its part file.</summary>
/// <param name="Slug">the file's key on its site</param>
/// <param name="ETag">the server's version of the file, so a changed file is not continued</param>
internal sealed record PartState(string Slug, string FileId, long Total, string? ETag, DateTimeOffset? LastModified,
                                 List<SegmentState> Segments);

/// <summary>
/// An unfinished download is "{name}.ferry-part" (the file, written at each part's offset) with
/// "{name}.ferry-part.json" (which bytes are done). Both go once the file is complete.
/// </summary>
public static class PartFile
{
    private const string Suffix = ".ferry-part";
    private const string StateSuffix = Suffix + ".json";

    public static string PartPath(string target) => target + Suffix;

    /// <summary>Whether <paramref name="target"/> has a part: a file, or a folder of them from a transport that fetches many.</summary>
    internal static bool PartExists(string target) => File.Exists(PartPath(target)) || Directory.Exists(PartPath(target));

    /// <summary>A part that is a folder holding one file becomes that file; a folder of more stays as it is.</summary>
    public static void BecomeOneFile(string part)
    {
        if (Directory.GetFileSystemEntries(part) is not [var only] || !File.Exists(only))
            return;
        var moved = part + ".one";
        File.Move(only, moved, overwrite: true);
        Directory.Delete(part);
        File.Move(moved, part);
    }

    /// <summary>Where a transport may keep how far it got with <paramref name="part"/>; it goes with the part.</summary>
    public static string ProgressPath(string part) => part + ".progress";

    internal static string StatePath(string target) => target + StateSuffix;

    /// <summary>The saved state of <paramref name="target"/>'s download, or null if there is none to continue.</summary>
    /// <exception cref="IOException">the state file is there but can't be read</exception>
    internal static PartState? Load(string target)
    {
        var path = StatePath(target);
        if (!File.Exists(path) || !PartExists(target))
            return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), FerryJson.Default.PartState);
        }
        catch (JsonException)
        {
            return null; // a state file cut short by a crash: the part can't be trusted, so the download starts over
        }
    }

    /// <summary>A part whose state file is there but unreadable (cut short by a crash): neither can be trusted.</summary>
    internal static bool IsDamaged(string target)
    {
        if (!File.Exists(StatePath(target)) || !PartExists(target))
            return false;
        try
        {
            return Load(target) is null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // in use, or unreadable for now: leave it alone
        }
    }

    /// <summary>Replace the saved state whole: a crash leaves the old state or the new one, never a torn one.</summary>
    /// <exception cref="IOException">the state can't be written</exception>
    internal static void Save(string target, PartState state) =>
        AtomicFile.WriteText(StatePath(target), JsonSerializer.Serialize(state, FerryJson.Default.PartState));

    /// <summary>Remove an unfinished download of <paramref name="target"/>.</summary>
    public static void Delete(string target)
    {
        if (Directory.Exists(PartPath(target)))
            Directory.Delete(PartPath(target), recursive: true);
        else
            File.Delete(PartPath(target));
        File.Delete(ProgressPath(PartPath(target)));
        AtomicFile.Delete(StatePath(target));
    }

    /// <summary><see cref="Delete"/>, as far as it goes: a file in use or a folder that's gone leaves the rest as it is.</summary>
    public static bool TryDelete(string target)
    {
        try
        {
            Delete(target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
