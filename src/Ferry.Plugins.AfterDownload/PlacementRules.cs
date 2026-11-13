using System.IO.Enumeration;
using Ferry.Core.Files;

namespace Ferry.Plugins.AfterDownload;

/// <summary>
/// The user's rules for where a file goes and what it is called, each "pattern=result", separated by semicolons, first match
/// wins. They apply when a file is about to start, so the queue always knows where it is.
/// </summary>
public static class PlacementRules
{
    /// <summary>"*.iso=D:\ISOs; *.mp4=%USERPROFILE%\Videos". A folder that isn't a full path is passed over.</summary>
    public static IReadOnlyList<(string Pattern, string Result)> Read(string rules) =>
        [.. rules.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(rule => rule.Split('=', 2, StringSplitOptions.TrimEntries))
                 .Where(parts => parts is [{ Length: > 0 }, { Length: > 0 }])
                 .Select(parts => (parts[0], parts[1]))];

    /// <summary>Where the first rule that matches says, if it is a full path; otherwise the folder as it was.</summary>
    public static string Folder(IReadOnlyList<(string Pattern, string Result)> rules, string name, string folder) =>
        FirstMatch(rules, name) is { } result && Environment.ExpandEnvironmentVariables(result) is var expanded && Path.IsPathFullyQualified(expanded) ? expanded : folder;

    /// <summary>What the first rule that matches says to call it, with {name} and {ext} from what it would be, made safe.</summary>
    public static string Name(IReadOnlyList<(string Pattern, string Result)> rules, string name)
    {
        if (FirstMatch(rules, name) is not { } result)
            return name;
        var renamed = FileNames.Sanitize(result.Replace("{name}", Path.GetFileNameWithoutExtension(name), StringComparison.Ordinal)
                                               .Replace("{ext}", Path.GetExtension(name), StringComparison.Ordinal));
        return renamed.Trim('.').Length > 0 ? renamed : name;
    }

    private static string? FirstMatch(IReadOnlyList<(string Pattern, string Result)> rules, string name)
    {
        foreach (var (pattern, result) in rules)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
                return result;
        }
        return null;
    }
}
