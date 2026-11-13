using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.RemoteQueues;

/// <summary>What the Remote Queues plugin adds to Ferry: aria2 or a Synology NAS does the downloading.</summary>
public static class RemoteQueuesPlugin
{
    private const string Aria2 = "aria2";
    private const string Synology = "synology";

    /// <summary>Off on a new install: while it is on, downloads don't happen on this PC.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_REMOTE_QUEUES", SettingType.Boolean, "false",
        "An aria2 daemon or a Synology NAS's Download Station does the downloading instead of this PC.")
    { Title = "Remote Queues" };

    private static readonly SettingKey Kind = new("FERRY_REMOTE_KIND", SettingType.Choice, Aria2, "What does the downloading.")
    {
        Title = "Downloads with",
        Choices = [Aria2, Synology],
    };

    private static readonly SettingKey Aria2Address = new("FERRY_ARIA2_URL", SettingType.Url, "http://localhost:6800/jsonrpc",
        "aria2's JSON-RPC address. Use https if it isn't on this PC: cookies for a file behind a login go to it.")
    { Title = "aria2 address" };

    private static readonly SettingKey SynologyAddress = new("FERRY_SYNOLOGY_URL", SettingType.Url, "https://diskstation.local:5001/",
        "Your NAS's address, as you open it in a browser.")
    { Title = "NAS address" };

    private static readonly SettingKey SynologyAccount = new("FERRY_SYNOLOGY_ACCOUNT", SettingType.Text, "",
        "An account on the NAS that can use Download Station, without two-step sign-in.")
    { Title = "NAS account" };

    private static readonly SettingKey SynologyFolder = new("FERRY_SYNOLOGY_FOLDER", SettingType.Text, "",
        "The shared folder to download to, such as downloads/ferry; its own default when empty.")
    { Title = "NAS folder" };

    private static readonly PluginSecret Token = new("aria2-token", "aria2 secret", "aria2's --rpc-secret, if it has one. Kept encrypted on this PC.");
    private static readonly PluginSecret SynologyPassword = new("synology-password", "NAS password", "The NAS account's password. Kept encrypted on this PC.");

    public static SitePlugin Plugin { get; } = new("Remote Queues", Enabled)
    {
        About = new(PluginGroup.Where, "\uE8CE", [], ["Synology's API is from community notes, with no two-step sign-in.", "Files needing a referer or login won't work on Synology.", "Album folders are ignored."]),
        Options = [Kind, Aria2Address, SynologyAddress, SynologyAccount, SynologyFolder],
        Secrets = [Token, SynologyPassword],
    };

    /// <summary>Which machine the settings say has the queue, to notice when that changes.</summary>
    public static string WhichOf(FerrySettings settings) =>
        settings.Text(Kind) == Synology ? $"{Synology}:{settings.Text(SynologyAddress)}:{settings.Text(SynologyAccount)}" : $"{Aria2}:{settings.Text(Aria2Address)}";

    /// <param name="sites">the catalog in effect, which turns a link into the address it is given</param>
    public static PolledQueue Queue(HttpFetcher http, FerrySettings settings, SecretStore secrets, Func<SiteCatalog?> sites)
    {
        var context = new PluginContext(http, settings, secrets);
        PolledQueue queue = settings.Text(Kind) == Synology
            ? new SynologyQueue(http, new Uri(settings.Text(SynologyAddress)), settings.Text(SynologyAccount), context.Secret(SynologyPassword), settings.Text(SynologyFolder), sites)
            : new Aria2Queue(http, new Uri(settings.Text(Aria2Address)), context.Secret(Token), sites);
        return queue;
    }
}
