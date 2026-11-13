using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.AfterDownload;

/// <summary>What the After Download plugin adds to Ferry: work on a file once it is complete, and rules for where it goes.</summary>
public static class AfterDownloadPlugin
{
    /// <summary>Off on a new install: it changes what ends up in the download folder.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_AFTER_DOWNLOAD", SettingType.Boolean, "false",
        "Scans finished files, unpacks archives, and moves and renames files by rules you set in its options.")
    { Title = "After Download" };

    public static readonly SettingKey Scan = new("FERRY_AFTER_SCAN", SettingType.Boolean, "true",
        "Have Microsoft Defender scan each finished file. One it finds a threat in isn't unpacked.")
    { Title = "Scan with Defender" };

    public static readonly SettingKey Extract = new("FERRY_AFTER_EXTRACT", SettingType.Boolean, "true",
        "Unpack finished .zip, .tar, .tar.gz, .tgz, .7z and .rar files into a folder beside them.")
    { Title = "Unpack archives" };

    public static readonly SettingKey DeleteArchive = new("FERRY_AFTER_DELETE_ARCHIVE", SettingType.Boolean, "false",
        "Delete an archive once it has been unpacked.")
    { Title = "Delete the archive afterwards" };

    public static readonly SettingKey SevenZip = new("FERRY_7ZIP_PATH", SettingType.Text, @"C:\Program Files\7-Zip\7z.exe",
        "7-Zip's 7z.exe, for .7z and .rar. Without it, the tar that comes with Windows is used, which reads most of them.")
    { Title = "7-Zip" };

    private static readonly SettingKey MoveRules = new("FERRY_MOVE_RULES", SettingType.Text, "",
        @"Where files go by name, first match wins: *.iso=D:\ISOs; *.mp4=%USERPROFILE%\Videos. Not for a file whose folder you chose.")
    { Title = "Move rules" };

    private static readonly SettingKey RenameRules = new("FERRY_RENAME_RULES", SettingType.Text, "",
        "What files are called by name, first match wins: *.jpeg={name}.jpg; report*=Work {name}{ext}. Not for a file you named yourself.")
    { Title = "Rename rules" };

    public static SitePlugin Plugin { get; } = new("After Download", Enabled)
    {
        About = new(PluginGroup.Around, "\uE73E", [], [".7z and .rar use 7-Zip, or Windows' tar, which doesn't read every rar.", "A password-protected archive fails.", "Rules don't touch a file you named or put somewhere yourself."]),
        Options = [Scan, Extract, DeleteArchive, SevenZip, MoveRules, RenameRules],
    };

    /// <summary>What the rules make of a file's folder and name; null while the plugin is off or there are none.</summary>
    public static Func<string, string, (string Folder, string Name)>? PlaceFor(FerrySettings settings)
    {
        if (!Plugin.IsOn(settings))
            return null;
        var (moves, renames) = (PlacementRules.Read(settings.Text(MoveRules)), PlacementRules.Read(settings.Text(RenameRules)));
        if (moves.Count == 0 && renames.Count == 0)
            return null;
        return (folder, name) => (PlacementRules.Folder(moves, name, folder), PlacementRules.Name(renames, name));
    }
}
