using Ferry.Core.Sites;
using Ferry.Plugins.Official;

namespace Ferry.Plugins.Torrents;

/// <summary>
/// What the Torrents plugin adds to Ferry: magnet links and .torrent files. The app has it, and a Linux agent built for it
/// (publish-agent.ps1); the Native AOT CLI on Windows can't carry the engine. Off on a new install: it shares with strangers
/// while it runs, and what is downloaded is the user's to answer for.
/// </summary>
public static class TorrentsPlugin
{
    public static SitePlugin Plugin { get; } = new("Torrents", TorrentKeys.Enabled)
    {
        About = new(PluginGroup.Links, "\uE72D", ["magnet: links", ".torrent files"], ["On a server a torrent of many files arrives as one .tar, and all its files are downloaded.", "A resumed torrent checks its data again.", "Only the overall speed limit applies."]),
        SiteIds = [TorrentSite.SiteId],
        Options = [TorrentKeys.AskFiles, TorrentKeys.UploadLimit],
        Sites = context => [new TorrentSite(context.Http, context.UserAgent, claimsFiles: context.Settings.Boolean(TorrentKeys.Enabled), context.Settings.SpeedLimit,
                                            context.Settings.Size(TorrentKeys.UploadLimit), packFolders: context.IsAgent)],
    };
}
