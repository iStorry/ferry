namespace Ferry.App.Services;

/// <summary>Folders as they are said where a whole path wouldn't fit.</summary>
public static class Folders
{
    /// <summary>"Downloads\\Ferry" for a folder in a folder; a folder at a drive's root as it is ("D:\\Ferry").</summary>
    public static string Shorten(string folder)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar));
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        return parent is null || Path.GetDirectoryName(parent) is null ? folder : $"{Path.GetFileName(parent)}\\{name}";
    }
}
