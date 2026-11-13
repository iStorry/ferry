using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Ferry.Core;

namespace Ferry.Plugins.DevReleases;

/// <summary>A system a release's files may be built for, as their names say it.</summary>
/// <param name="Name">which, whatever the machine</param>
/// <param name="Words">what its files are named with</param>
/// <param name="Installers">its own kinds of file, the most wanted first</param>
/// <param name="Archives">the archives its users expect</param>
/// <param name="Arm">an ARM machine, not an x64 one</param>
public sealed record TargetSystem(string Name, HashSet<string> Words, string[] Installers, string[] Archives, bool Arm)
{
    public static TargetSystem Windows { get; } = new("Windows", ["windows", "win", "win32", "win64", "msvc", "mingw"], [".exe", ".msi"], [".zip", ".7z"], Arm: false);

    public static TargetSystem Linux { get; } =
        new("Linux", ["linux", "ubuntu", "debian", "fedora", "centos", "alpine", "gnu", "musl"], [".appimage", ".deb", ".rpm", ".snap", ".flatpak"], [".tar.gz", ".tar.xz", ".tgz", ".tar.bz2", ".zip"], Arm: false);

    public static TargetSystem Mac { get; } = new("macOS", ["mac", "macos", "osx", "darwin", "apple"], [".dmg", ".pkg"], [".zip", ".tar.gz"], Arm: false);

    public static IReadOnlyList<TargetSystem> All { get; } = [Windows, Linux, Mac];

    /// <summary>The other systems, whatever machine this one is for.</summary>
    public IEnumerable<TargetSystem> Others => All.Where(other => other.Name != Name);

    /// <summary>The machine this runs on.</summary>
    public static TargetSystem Here { get; } =
        (OperatingSystem.IsWindows() ? Windows : OperatingSystem.IsMacOS() ? Mac : Linux) with { Arm = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm };
}

/// <summary>Which of a release's files a link to the whole release means: the one for a system, when its name makes that clear.</summary>
internal static partial class ReleaseAssets
{
    private const int MostNamesShown = 6;
    private const int ForTheSystem = 4;
    private const int ForTheMachine = 2;
    private const int ForOtherBits = -1;
    private const int ForOtherMachine = -3;
    private const int NotTheProgramItself = -4;
    private const int AnArchive = 1;

    private static readonly string[] NotDownloads =
        [".sha1", ".sha256", ".sha256sum", ".sha512", ".md5", ".sig", ".asc", ".pem", ".crt", ".cert", ".sbom", ".spdx", ".json", ".txt", ".yml", ".yaml", ".blockmap"];

    private static readonly string[] NoSystemHereFiles = [".apk", ".ipa"];
    private static readonly HashSet<string> NoSystemHere = ["android", "ios", "freebsd", "openbsd", "netbsd"];

    private static readonly HashSet<string> SixtyFourBit = ["x64", "amd64", "win64", "64bit"];
    private static readonly HashSet<string> ThirtyTwoBit = ["x86", "i386", "i686", "win32", "32bit"];
    private static readonly HashSet<string> Arm = ["arm", "arm64", "armv7", "aarch64"];
    private static readonly HashSet<string> NotTheProgram = ["debug", "symbols", "pdb", "src", "source"];

    /// <summary>The name to download for <paramref name="system"/>, out of a release's file names.</summary>
    /// <exception cref="StageException">nothing to download, or no way to tell which file is meant; says which there are</exception>
    public static string Pick(IReadOnlyList<string> names, TargetSystem system)
    {
        if (names.Count == 0)
            throw new StageException(Stage.Resolve, "This release has no files.");
        var candidates = names.Where(name => !HasEnding(name, NotDownloads) && !IsForAnotherSystem(name, system)).ToList();
        if (candidates.Count == 1)
            return candidates[0];
        var scored = candidates.Select(name => (Name: name, Score: Score(name, system))).ToList();
        var best = scored.Select(entry => entry.Score).DefaultIfEmpty(int.MinValue).Max();
        return scored.Where(entry => entry.Score == best).ToList() is [var winner] ? winner.Name : throw new StageException(Stage.Resolve, CantTell(names));
    }

    private static string CantTell(IReadOnlyList<string> names)
    {
        var shown = string.Join(", ", names.Take(MostNamesShown));
        var more = names.Count > MostNamesShown ? $" and {names.Count - MostNamesShown} more" : "";
        return $"This release has {names.Count} files, and Ferry can't tell which one you want: {shown}{more}. Copy the link of that file.";
    }

    private static bool IsForAnotherSystem(string name, TargetSystem system)
    {
        var words = Words(name);
        return HasEnding(name, NoSystemHereFiles) || words.Overlaps(NoSystemHere)
            || system.Others.Any(other => HasEnding(name, other.Installers) || words.Overlaps(other.Words));
    }

    private static int Score(string name, TargetSystem system)
    {
        var words = Words(name);
        var score = words.Overlaps(system.Words) || HasEnding(name, system.Installers) ? ForTheSystem : 0;
        var (thisMachine, otherBits, otherMachine) = system.Arm ? (Arm, ThirtyTwoBit, SixtyFourBit) : (SixtyFourBit, ThirtyTwoBit, Arm);
        score += words.Overlaps(thisMachine) ? ForTheMachine : words.Overlaps(otherMachine) ? ForOtherMachine : words.Overlaps(otherBits) ? ForOtherBits : 0;
        score += words.Overlaps(NotTheProgram) ? NotTheProgramItself : 0;
        return score + (Rank(name, system.Installers) is { } rank ? AnArchive + system.Installers.Length - rank : HasEnding(name, system.Archives) ? AnArchive : 0);
    }

    /// <summary>Where the file's kind comes in <paramref name="endings"/>, 0 for the most wanted; null when it is none of them.</summary>
    private static int? Rank(string name, string[] endings) =>
        Array.FindIndex(endings, ending => name.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) is >= 0 and var rank ? rank : null;

    private static bool HasEnding(string name, string[] endings) => endings.Any(ending => name.EndsWith(ending, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> Words(string name) =>
        [.. NotWordOrDigit().Split(SixtyFourBitSpelling().Replace(name, "x64").ToLowerInvariant())];

    [GeneratedRegex("x86[-_]64", RegexOptions.IgnoreCase)]
    private static partial Regex SixtyFourBitSpelling();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotWordOrDigit();
}
