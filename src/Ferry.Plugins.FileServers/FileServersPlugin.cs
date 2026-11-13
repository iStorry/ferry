using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.FileServers;

/// <summary>What the FTP and SFTP plugin adds to Ferry: ftp://, ftps:// and sftp:// links.</summary>
public static class FileServersPlugin
{
    /// <summary>On a new install too: these links can't be anything else, and all it asks of is the server in the link.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_FILE_SERVERS", SettingType.Boolean, "true",
        "ftp://, ftps:// and sftp:// links.")
    { Title = "FTP and SFTP" };

    public static SitePlugin Plugin { get; } = new("FTP and SFTP", Enabled)
    {
        BuiltIn = true,
        About = new(PluginGroup.Links, "\uE968", ["ftp://", "ftps://", "sftp://"], ["A password goes in a Password: header, under Login, when adding.", "SFTP needs an SSH key and a known host."]),
        SiteIds = [FileServerSite.SiteId],
        Sites = context => [new FileServerSite(context.UserAgent)],
    };
}
