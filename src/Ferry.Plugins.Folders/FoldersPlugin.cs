using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Folders;

/// <summary>What the Folders and Albums plugin adds to Ferry: a link to a shared folder adds each file in it.</summary>
public static class FoldersPlugin
{
    /// <summary>On a new install too: all it asks of is the link's own service. Off, the link is refused as a folder.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_FOLDERS", SettingType.Boolean, "true",
        "Links to Google Drive folders and Pixeldrain lists add each file in them.")
    { Title = "Folders and Albums" };

    public static SitePlugin Plugin { get; } = new("Folders and Albums", Enabled)
    {
        BuiltIn = true,
        About = new(PluginGroup.Links, "\uE8B7", ["Google Drive folders", "Pixeldrain lists"], ["Drive's plain folder page isn't a documented API, so it can change."]),
        Listers = context => [new DriveFolderLister(context.Http, context.UserAgent), new PixeldrainListLister(context.Http, context.UserAgent)],
    };
}
