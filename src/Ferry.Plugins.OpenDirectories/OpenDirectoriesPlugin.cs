using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.OpenDirectories;

/// <summary>What the Open Directories plugin adds to Ferry: a server's listing of a folder adds each file in it.</summary>
public static class OpenDirectoriesPlugin
{
    /// <summary>On a new install too: otherwise a listing is saved as a web page, which nobody wants.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_OPEN_DIRECTORIES", SettingType.Boolean, "true",
        "A link to a web server's listing of a folder adds each file in it.")
    { Title = "Open Directories" };

    public static SitePlugin Plugin { get; } = new("Open Directories", Enabled)
    {
        BuiltIn = true,
        About = new(PluginGroup.Links, "\uE8FD", ["Apache listings", "nginx listings"], ["Up to 4 folders down and 5000 files, all in one folder.", "Names that repeat across folders get \"(1)\"."]),
        Listers = context => [new DirectoryLister(context.Http, context.UserAgent)],
    };
}
