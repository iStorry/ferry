using System.Text;
using System.Text.RegularExpressions;

namespace Ferry.Core.Library;

/// <summary>A title as a site lists it, read into its name and version by the plugin that knows the site: "Blue Harbor 2" at "v2.32.2".</summary>
/// <param name="Version">null when the site gives none</param>
public sealed record FoundTitle(string Name, string? Version);

/// <summary>How a game here stands against what its site lists.</summary>
public enum TitleState
{
    /// <summary>The site has nothing under its name, or it has no version to compare.</summary>
    Unknown,
    UpToDate,

    /// <summary>The newest version there is another than this one.</summary>
    Outdated,
}

/// <summary>Matching games here to the titles a site lists, by name and then version.</summary>
public static partial class TitleMatch
{
    /// <summary>
    /// A season at the front of a version belongs to the name: "Abc [S3 P4b]" is "Abc Season 3" at "P4b". Unless the name
    /// says that season already, as a part's "Season 3 (S3 P4b)" does.
    /// </summary>
    public static (string Name, string? Version) MoveSeason(string name, string? version)
    {
        if (version is null || SeasonFirst().Match(version) is not { Success: true } season)
            return (name, version);
        var number = season.Groups["number"].Value.TrimStart('0') is { Length: > 0 } n ? n : "0";
        var said = Words(name) is [.., "season", var last] && last.TrimStart('0') == number.TrimStart('0');
        return (said ? name : $"{name} Season {number}", season.Groups["rest"].Value.Trim());
    }

    [GeneratedRegex(@"^\s*(?:s|season)\s*\.?\s*(?<number>\d+)\b[\s,:;\-]*(?<rest>\S.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFirst();

    /// <summary>
    /// A game, or a part of one, against what a search for the game's name found. A part matches a title that is the game's
    /// name and the part's ("Abc Season 1"), or the part's alone; either way with or without words such as "Season", so
    /// "Abc/Season 1" is also "Abc 1". The first part is also the game's name alone: "Abc/Season 1" is "Abc". The first
    /// title that matches is taken as the newest.
    /// </summary>
    /// <param name="game">the game the search was for</param>
    /// <param name="part">one of its parts, or null for the game itself</param>
    public static TitleState Check(LibraryEntry game, LibraryEntry? part, IReadOnlyList<FoundTitle> found)
    {
        var local = part ?? game;
        if (local.Version is null)
            return TitleState.Unknown;
        var names = part is null ? Names(game.Name) : PartNames(game, part);
        var newest = found.FirstOrDefault(entry => entry.Version is not null && Names(entry.Name).Overlaps(names));
        return newest is null ? TitleState.Unknown
            : SameVersion(newest.Version!, local.Version) ? TitleState.UpToDate
            : TitleState.Outdated;
    }

    /// <summary>Whether anything found is the game, or one of its parts, whatever its version.</summary>
    public static bool Finds(LibraryEntry game, IReadOnlyList<FoundTitle> found)
    {
        var names = Names(game.Name);
        foreach (var part in game.Parts)
            names.UnionWith(PartNames(game, part));
        return found.Any(entry => entry.Version is not null && Names(entry.Name).Overlaps(names));
    }

    /// <summary>What a part of a game is called on the site: "Abc Season 1", "Abc 1", "Season 1", and for the first, "Abc".</summary>
    private static HashSet<string> PartNames(LibraryEntry game, LibraryEntry part)
    {
        var names = Names($"{game.Name} {part.Name}");
        names.UnionWith(Names(part.Name));
        if (Words(part.Name).Where(word => !SeriesWords.Contains(word)).ToList() is [var number] && First.Contains(number))
            names.UnionWith(Names(game.Name));
        return names;
    }

    // what a first season or part is numbered
    private static readonly HashSet<string> First = new(StringComparer.Ordinal) { "1", "01", "one", "i" };

    /// <summary>A name as it is compared: letters and digits only, in lower case, so "Blue Harbor: Two" is "Blue Harbor Two".</summary>
    public static string Key(string name) => string.Concat(Words(name));

    // what numbers a game's seasons or parts, and is left out of a name as often as not: "Abc Season 1" is "Abc 1"
    private static readonly HashSet<string> SeriesWords = new(StringComparer.Ordinal)
    {
        "season", "part", "pt", "chapter", "episode", "book", "volume", "vol", "act", "ch", "ep",
    };

    /// <summary>
    /// A name's <see cref="Key"/>, and its key without words such as "Season"; both also without "The", "A" or "An" in
    /// front, so "The Quiet Harbor Pro" is "Quiet Harbor Pro".
    /// </summary>
    private static HashSet<string> Names(string name)
    {
        var words = Words(name);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var these in words is [var first, _, ..] && Articles.Contains(first) ? [words, words[1..]] : new[] { words })
        {
            names.Add(string.Concat(these));
            names.Add(string.Concat(these.Where(word => !SeriesWords.Contains(word))));
        }
        names.RemoveWhere(key => !key.Any(char.IsLetter)); // a part's "Season 1" alone is "1", which would be any game's
        return names;
    }

    /// <summary>
    /// A name's words, in lower case, without what is between them. A season or part written together with its number is
    /// two words, so "Abc S3" is "Abc Season 3" and "Ep2" is "Ep 2".
    /// </summary>
    private static List<string> Words(string name)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormKC).Append(' '))
        {
            if (char.IsLetterOrDigit(c))
                word.Append(char.ToLowerInvariant(c));
            else if (word.Length > 0)
            {
                if (Numbered().Match(word.ToString()) is { Success: true } numbered)
                    words.AddRange([numbered.Groups["word"].Value is "s" ? "season" : numbered.Groups["word"].Value, numbered.Groups["number"].Value]);
                else
                    words.Add(word.ToString());
                word.Clear();
            }
        }
        return words;
    }

    [GeneratedRegex(@"^(?<word>s|season|ep|episode|ch|chapter|vol|volume|part|pt|book|act)(?<number>\d+)$")]
    private static partial Regex Numbered();

    // words a search does without, and that a shorter search doesn't end on
    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "by", "for", "from", "in", "into", "of", "on", "or", "the", "to", "with",
    };

    // what a name can start with on one side and not the other
    private static readonly HashSet<string> Articles = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an" };

    /// <summary>
    /// What to search the site for, in turn, until something found is the game: its whole name, then without its first word,
    /// then without its last, then the words before the first small one, then the first word that isn't small. A shorter
    /// search doesn't end on a small word, nor start on one but "The", "A" or "An". The site's search can find nothing for
    /// "Keepers of Dawn" and the game for "Keepers"; "The Quiet Harbor Pro" is searched as itself, "Quiet Harbor Pro",
    /// "The Quiet Harbor", and "Quiet".
    /// </summary>
    public static IReadOnlyList<string> Searches(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var shorter = new List<string[]>();
        if (words.Length > 1)
            shorter.AddRange([words[1..], words[..^1]]);
        var beforeSmall = words.TakeWhile((word, at) => at == 0 || !SmallWords.Contains(word)).ToArray();
        if (beforeSmall.Length < words.Length)
            shorter.Add(beforeSmall);
        if (words.FirstOrDefault(word => !SmallWords.Contains(word)) is { } firstWord)
            shorter.Add([firstWord]);
        var searches = new List<string> { name.Trim() };
        searches.AddRange(shorter.Select(Trimmed).Select(these => string.Join(' ', these)));
        // a search of one or two letters, or of small words only, would list half the site
        return [.. searches.Where(search => Key(search).Length >= 3 && search.Split(' ').Any(word => !SmallWords.Contains(word)))
                           .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Without small words at the end, nor at the start but an article: "of Abyss" is "Abyss", "Agents of" is "Keepers".</summary>
    private static string[] Trimmed(string[] words)
    {
        var (from, to) = (0, words.Length);
        while (from < to && SmallWords.Contains(words[from]) && !Articles.Contains(words[from]))
            from++;
        while (to > from && SmallWords.Contains(words[to - 1]))
            to--;
        return words[from..to];
    }

    /// <summary>
    /// "v0.9.6", "V0.9.6", "0.9.6" and "v0.9.6 Reworked" are the same version, as are "vP4b" and "P4b", and "Final" is "v1.0": a game is often
    /// renamed from one to the other when it is finished.
    /// </summary>
    public static bool SameVersion(string one, string other) => VersionKey(one) == VersionKey(other);

    private static readonly HashSet<string> Finished = new(StringComparer.Ordinal) { "final", "1", "1.0", "1.00", "1.0.0" };

    /// <summary>
    /// The version's number, the first word with a digit in it, so words around it don't count: "v0.1 Reworked" is "0.1".
    /// A version with no number at all ("Final") is compared as it is.
    /// </summary>
    private static string VersionKey(string version)
    {
        var words = version.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var key = words.FirstOrDefault(word => word.Any(char.IsDigit)) ?? string.Concat(words);
        key = key.StartsWith('v') && key.Length > 1 && key[1..].Any(char.IsDigit) ? key[1..] : key; // "vP4b" is "P4b"
        return Finished.Contains(key) ? "1.0" : key;
    }
}
