namespace Ferry.Core.Files;

public enum FileKind
{
    Video,
    Image,
    Archive,
    Audio,
    Document,
    Program,
    Other,
}

/// <summary>What kind of file a name is, by its extension: for icons, and for sorting downloads into folders.</summary>
public static class FileKinds
{
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".ts" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar", ".gz" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".m4a", ".wav", ".ogg", ".opus", ".aac" };
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".epub" };
    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".msix", ".iso", ".dmg", ".deb", ".rpm", ".appimage" };

    /// <param name="name">a file name or path</param>
    public static FileKind Of(string name)
    {
        var extension = Path.GetExtension(name);
        return Videos.Contains(extension) ? FileKind.Video
               : Images.Contains(extension) ? FileKind.Image
               : Archives.Contains(extension) ? FileKind.Archive
               : Audio.Contains(extension) ? FileKind.Audio
               : Documents.Contains(extension) ? FileKind.Document
               : Programs.Contains(extension) ? FileKind.Program
               : FileKind.Other;
    }

    /// <summary>Where a file of this name goes when sorted by type, inside <paramref name="folder"/>.</summary>
    public static string SortedInto(string folder, string name) =>
        FolderFor(Of(name)) is { } kind ? Path.Combine(folder, kind) : folder;

    /// <summary>The folder a kind is sorted into, by name; null for what stays where it is.</summary>
    public static string? FolderFor(FileKind kind) => kind switch
    {
        FileKind.Video => "Videos",
        FileKind.Image => "Images",
        FileKind.Archive => "Archives",
        FileKind.Audio => "Music",
        FileKind.Document => "Documents",
        FileKind.Program => "Programs",
        _ => null,
    };
}
