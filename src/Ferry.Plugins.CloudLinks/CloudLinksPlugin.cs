using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.CloudLinks;

/// <summary>What the Cloud Links plugin adds to Ferry: share links that lead to a web page about a file, downloaded as the file.</summary>
public static class CloudLinksPlugin
{
    /// <summary>On a new install too: without it, these links can only be refused, and all it asks of is the link's own service.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_CLOUD_LINKS", SettingType.Boolean, "true",
        "Share links from Google Drive, Dropbox and OneDrive.")
    { Title = "Cloud Links" };

    public static SitePlugin Plugin { get; } = new("Cloud Links", Enabled)
    {
        BuiltIn = true,
        About = new(PluginGroup.Links, "\uE753", ["drive.google.com", "dropbox.com", "onedrive.live.com", "sharepoint.com"], ["Personal OneDrive links: Microsoft's API has refused newer ones."]),
        SiteIds = [GoogleDriveSite.SiteId, DropboxSite.SiteId, OneDriveSite.SiteId],
        Sites = context => [new GoogleDriveSite(context.Http, context.UserAgent), DropboxSite.Create(context.Http, context.UserAgent), new OneDriveSite(context.Http, context.UserAgent)],
    };
}
