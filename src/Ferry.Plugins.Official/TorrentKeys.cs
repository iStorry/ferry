using Ferry.Core.Settings;

namespace Ferry.Plugins.Official;

/// <summary>
/// The Torrents plugin's settings. They live here, not with it, as a CLI built without the engine reads the same settings file
/// and would otherwise warn about each of them.
/// </summary>
public static class TorrentKeys
{
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_TORRENTS", SettingType.Boolean, "false",
        "Magnet links and .torrent files. Pieces are shared with others during a download, as torrents do, and sharing stops when it is done. Only download what you have the right to.")
    { Title = "Torrents" };

    public static readonly SettingKey UploadLimit = new("FERRY_TORRENT_UPLOAD_LIMIT", SettingType.Size, "0",
        "The most to share per second during a download, such as 500K; 0 for no limit. Sharing less can make a download slower.")
    { Title = "Sharing limit" };

    public static readonly SettingKey AskFiles = new("FERRY_TORRENT_ASK_FILES", SettingType.Boolean, "true",
        "Ask which of a torrent's files to download when one is added.")
    { Title = "Ask which files" };

    public static IReadOnlyList<SettingKey> All { get; } = [Enabled, UploadLimit, AskFiles];
}
