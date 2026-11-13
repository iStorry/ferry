using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.MediaSites;

/// <summary>What the Media Sites plugin adds to Ferry: video pages, through yt-dlp, which the user installs themselves.</summary>
public static class MediaSitesPlugin
{
    /// <summary>Off on a new install: it needs another program, and whether a site allows this is for the user to judge.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_MEDIA_SITES", SettingType.Boolean, "false",
        "Video pages on the sites you list, through yt-dlp, which you install yourself.")
    { Title = "Media Sites" };

    private static readonly SettingKey ToolPath = new("FERRY_YTDLP_PATH", SettingType.Text, "yt-dlp",
        "The yt-dlp program: its full path, or just its name if it is on the PATH.")
    { Title = "yt-dlp" };

    private static readonly SettingKey Hosts = new("FERRY_MEDIA_HOSTS", SettingType.Hosts, "youtube.com,youtu.be,vimeo.com,dailymotion.com,soundcloud.com,twitch.tv",
        "Which sites' links go to yt-dlp.")
    { Title = "Sites" };

    private static readonly SettingKey Format = new("FERRY_MEDIA_FORMAT", SettingType.Text, "b",
        "What to ask yt-dlp for, as its -f option. \"b\" is the best single file, which needs nothing else; better quality on some sites needs ffmpeg.")
    { Title = "Format" };

    public static SitePlugin Plugin { get; } = new("Media Sites", Enabled)
    {
        About = new(PluginGroup.Links, "\uE8B2", ["Video pages on the sites you list"], ["Best single file by default; more needs ffmpeg and a changed Format."]),
        SiteIds = [MediaSite.SiteId],
        KeepsLinksWhileOff = false,
        Options = [ToolPath, Hosts, Format],
        Sites = context => [new MediaSite(context.Settings.Text(ToolPath), context.Settings.Text(Format), context.UserAgent,
                                          [.. context.Settings.Text(Hosts).Split(',', StringSplitOptions.RemoveEmptyEntries)])],
    };
}
