using System.Text.RegularExpressions;

namespace Ferry.Core.Library;

/// <summary>A game in the folder, or a part of one.</summary>
/// <param name="Name">"The Long Road", or a part's "Season 1"</param>
/// <param name="Version">"v0.9.6", as it is written; null when the name gives none</param>
/// <param name="Path">its folder or archive</param>
/// <param name="Changed">when it was last written to</param>
/// <param name="Parts">for a folder with no version of its own, such as "Abc", what is in it with one: "Season 1 (v1.0)", "Season 2 (v0.1)"</param>
public sealed record LibraryEntry(string Name, string? Version, string Path, bool IsFolder, DateTime Changed, IReadOnlyList<LibraryEntry> Parts);

/// <summary>Reads a folder of games, each named "{Name} ({version})" or "{Name} [{version}]", or a folder of such parts.</summary>
public static partial class LibraryFolder
{
    // what a game can come as besides a folder of its own
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar", ".gz", ".apk" };

    private static readonly EnumerationOptions Entries = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false,
    };

    /// <summary>A name and its version, from a folder's name or an archive's without its extension.</summary>
    public static (string Name, string? Version) Split(string entry)
    {
        var match = Versioned().Match(entry);
        return match.Success && match.Groups["name"].Value.Trim() is { Length: > 0 } name
            ? TitleMatch.MoveSeason(name, match.Groups["version"].Value.Trim()) // "Abc (S3 P4b)" is "Abc Season 3" at "P4b"
            : (entry.Trim(), null);
    }

    /// <summary>
    /// Every folder in it, and every archive, by name. A folder whose name has no version is looked into once: if anything
    /// there has one, all its folders and archives are its parts.
    /// </summary>
    /// <exception cref="IOException">it couldn't be read, or isn't there</exception>
    /// <exception cref="UnauthorizedAccessException"></exception>
    public static IReadOnlyList<LibraryEntry> Read(string folder)
    {
        var games = new List<LibraryEntry>();
        foreach (var game in EntriesIn(folder))
            games.Add(game is { IsFolder: true, Version: null } ? game with { Parts = PartsOf(game.Path) } : game);
        return Sorted(games);
    }

    /// <summary>What is in a folder with no version of its own, when any of it has one; nothing when it is a game's own files.</summary>
    private static List<LibraryEntry> PartsOf(string folder)
    {
        try
        {
            var parts = EntriesIn(folder).ToList();
            return parts.Any(part => part.Version is not null) ? Sorted(parts) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return []; // listed as it is, without its parts
        }
    }

    private static IEnumerable<LibraryEntry> EntriesIn(string folder)
    {
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Entries))
        {
            var isFolder = entry is DirectoryInfo;
            if (!isFolder && !Archives.Contains(entry.Extension))
                continue;
            var (name, version) = Split(isFolder ? entry.Name : System.IO.Path.GetFileNameWithoutExtension(entry.Name));
            yield return new LibraryEntry(name, version, entry.FullName, isFolder, entry.LastWriteTime, []);
        }
    }

    private static List<LibraryEntry> Sorted(IEnumerable<LibraryEntry> games) =>
        [.. games.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(game => game.Version, StringComparer.OrdinalIgnoreCase)];

    // the last bracket of the name, in round or square brackets: "The Long Road (v0.9.6)"
    [GeneratedRegex(@"^(?<name>.*?)\s*(?:\((?<version>[^()]+)\)|\[(?<version>[^\[\]]+)\])\s*$")]
    private static partial Regex Versioned();
}
