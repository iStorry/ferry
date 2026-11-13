using System.Runtime.CompilerServices;
using Ferry.Core.Http;
using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.FileHosts;

/// <summary>What the File Hosts plugin adds to Ferry: links to a host's page about a file, or a folder of them, downloaded as the files.</summary>
public static class FileHostsPlugin
{
    /// <summary>Off on a new install: these are sites to choose to use, and they change often.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_FILE_HOSTS", SettingType.Boolean, "false",
        "File links from Pixeldrain, MediaFire and Cyberdrop, and Gofile folders and Cyberdrop albums.")
    { Title = "File Hosts" };

    // the site and the lister are made apart, for the catalog and for the expander, but share one guest account and the folders it has read
    private static readonly ConditionalWeakTable<HttpFetcher, GofileApi> GofileApis = new();

    public static SitePlugin Plugin { get; } = new("File Hosts", Enabled)
    {
        About = new(PluginGroup.Links, "\uE896", ["pixeldrain.com", "mediafire.com", "gofile.io", "cyberdrop.me"], ["Gofile and Cyberdrop use what their own sites use, not documented APIs, so they are the most likely to break.", "Gofile can't go through aria2 or Synology, as it needs a cookie."]),
        SiteIds = [PixeldrainSite.SiteId, MediaFireSite.SiteId, GofileSite.SiteId, CyberdropSite.SiteId],
        Sites = context => [PixeldrainSite.Create(context.Http, context.UserAgent), new MediaFireSite(context.Http, context.UserAgent),
                            new GofileSite(context.UserAgent, Gofile(context)), new CyberdropSite(context.Http, context.UserAgent)],
        Listers = context => [new GofileLister(Gofile(context)), new CyberdropLister(context.Http, context.UserAgent)],
    };

    private static GofileApi Gofile(PluginContext context) => GofileApis.GetValue(context.Http, http => new GofileApi(http, context.UserAgent));
}
