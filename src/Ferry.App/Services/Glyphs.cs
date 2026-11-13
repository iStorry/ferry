using Ferry.Core.Files;

namespace Ferry.App.Services;

/// <summary>The Segoe Fluent icons rows share: a file's kind.</summary>
public static class Glyphs
{
    public const string Server = "\uE839";
    public const string Pc = "\uE7F8";

    /// <summary>A machine on the network with its own queue: aria2, a NAS.</summary>
    public const string RemoteQueue = "\uEDA2";

    public const string Video = "\uE714";
    public const string Image = "\uEB9F";
    public const string Archive = "\uE7B8";
    public const string Audio = "\uE8D6";
    public const string Document = "\uE8A5";
    public const string Program = "\uE74C";
    public const string OtherFile = "\uE7C3";

    /// <summary>Where downloads run.</summary>
    public static string For(DownloadPlace place) => place switch
    {
        DownloadPlace.Server => Server,
        DownloadPlace.RemoteQueue => RemoteQueue,
        _ => Pc,
    };

    public static string For(FileKind kind) => kind switch
    {
        FileKind.Video => Video,
        FileKind.Image => Image,
        FileKind.Archive => Archive,
        FileKind.Audio => Audio,
        FileKind.Document => Document,
        FileKind.Program => Program,
        _ => OtherFile,
    };

    /// <summary>The icon for a file name or path, by its extension.</summary>
    public static string ForFile(string name) => For(FileKinds.Of(name));
}
