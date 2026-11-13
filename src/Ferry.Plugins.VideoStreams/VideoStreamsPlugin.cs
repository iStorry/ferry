using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.VideoStreams;

/// <summary>What the Video Streams plugin adds to Ferry: HLS and DASH links, downloaded as one video. Streams protected by DRM are refused.</summary>
public static class VideoStreamsPlugin
{
    /// <summary>On a new install too: otherwise the playlist's few lines of text are saved as the file.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_VIDEO_STREAMS", SettingType.Boolean, "true",
        "Links to .m3u8 and .mpd playlists are downloaded as one video, in the best quality offered.")
    { Title = "Video Streams" };

    internal static readonly SettingKey Ffmpeg = new("FERRY_FFMPEG_PATH", SettingType.Text, "ffmpeg",
        "The ffmpeg program, its full path or just its name if it is on the PATH. A video whose picture and sound are separate files needs it, and you install it yourself.")
    { Title = "ffmpeg" };

    public static SitePlugin Plugin { get; } = new("Video Streams", Enabled)
    {
        BuiltIn = true,
        About = new(PluginGroup.Links, "\uE714", [".m3u8 links", ".mpd links"], ["DASH with separate picture and sound needs ffmpeg.", "DASH is only ever read from its first period.", "DRM and live streams are refused."]),
        SiteIds = [HlsSite.SiteId, DashSite.SiteId],
        Options = [Ffmpeg],
        Sites = context => [new HlsSite(context.Http, context.UserAgent, context.Settings.Text(Ffmpeg)), new DashSite(context.Http, context.UserAgent, context.Settings.Text(Ffmpeg))],
    };
}
