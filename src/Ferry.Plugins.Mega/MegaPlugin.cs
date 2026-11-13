using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Mega;

/// <summary>What the Mega plugin adds to Ferry: Mega's file and folder links, decrypted as they arrive with the key in the link.</summary>
public static class MegaPlugin
{
    /// <summary>On a new install too: without it, these links can only be refused, and all it asks of is Mega.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_MEGA", SettingType.Boolean, "true",
        "Mega's file and folder links, decrypted as they arrive with the key in the link.")
    { Title = "Mega" };

    public static SitePlugin Plugin { get; } = new("Mega", Enabled)
    {
        BuiltIn = true,
        About = new(PluginGroup.Links, "\uE72E", ["mega.nz files", "mega.nz folders"], ["Mega's integrity check isn't made.", "One connection per file.", "A folder's subfolders end up flat."]),
        SiteIds = [MegaSite.SiteId],
        Sites = context => [new MegaSite(context.Http, context.UserAgent)],
        Listers = context => [new MegaFolderLister(new MegaApi(context.Http, context.UserAgent))],
    };
}
