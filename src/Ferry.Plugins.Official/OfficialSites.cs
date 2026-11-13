using System.Diagnostics.CodeAnalysis;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.AfterDownload;
using Ferry.Plugins.BrowserLink;
using Ferry.Plugins.CloudLinks;
using Ferry.Plugins.Debrid;
using Ferry.Plugins.DevReleases;
using Ferry.Plugins.Feeds;
using Ferry.Plugins.FileHosts;
using Ferry.Plugins.FileServers;
using Ferry.Plugins.Folders;
using Ferry.Plugins.MediaSites;
using Ferry.Plugins.Mega;
using Ferry.Plugins.Notifications;
using Ferry.Plugins.OpenDirectories;
using Ferry.Plugins.RemoteQueues;
using Ferry.Plugins.VideoStreams;

namespace Ferry.Plugins.Official;

/// <summary>The plugins compiled in, besides Server Support with its own page: what the app and the agent build from.</summary>
public static class OfficialSites
{
    // where the agent downloads, for what it says of a link nothing there takes
    private const string OnTheServer = "on the server";

    private static readonly Dictionary<string, string> NoneSent = [];

    // replaced whole on each change, never changed: a plugin is installed or removed while others read these
    private static readonly Lock Changing = new();

    private static volatile SitePlugin[] s_all =
    [
        DebridPlugin.Plugin, CloudLinksPlugin.Plugin, DevReleasesPlugin.Plugin, FileHostsPlugin.Plugin, FoldersPlugin.Plugin, OpenDirectoriesPlugin.Plugin,
        VideoStreamsPlugin.Plugin, MediaSitesPlugin.Plugin, MegaPlugin.Plugin, FileServersPlugin.Plugin,
        FeedsPlugin.Plugin, AfterDownloadPlugin.Plugin, NotificationsPlugin.Plugin, RemoteQueuesPlugin.Plugin, BrowserLinkPlugin.Plugin,
    ];

    /// <summary>In the order they are asked. Debrid goes first, as its links would be another's without it.</summary>
    public static IReadOnlyList<SitePlugin> Plugins => s_all;

    /// <summary>Puts their settings in the one table. Once, as the process starts, before any settings are read.</summary>
    /// <param name="alsoHere">plugins only some builds have, as they can't go in the Native AOT helper on Windows</param>
    public static void Register(params IEnumerable<SitePlugin> alsoHere)
    {
        lock (Changing)
            s_all = [.. s_all, .. alsoHere.Except(s_all)];
        SettingKey.Contribute(TorrentKeys.All);
        foreach (var plugin in Plugins)
            plugin.Register();
    }

    /// <summary>
    /// A plugin someone installed, beside those that come with Ferry, before its settings are read. False, with the reason,
    /// when it would take the place of something that is there already.
    /// </summary>
    public static bool TryAdd(SitePlugin plugin, [NotNullWhen(false)] out string? reason)
    {
        lock (Changing)
        {
            reason = s_all.Any(other => other.Name == plugin.Name) ? $"There is a plugin called {plugin.Name} already."
                : plugin.SiteIds.FirstOrDefault(id => id == DirectSite.SiteId || s_all.Any(other => other.Owns(id))) is { } site ? $"Its site \"{site}\" is another plugin's."
                : plugin.Options.Prepend(plugin.Enabled).FirstOrDefault(key => SettingKey.TryFind(key.Name, out var known) && known != key) is { } key ? $"Its setting {key.Name} is there already."
                : null;
            if (reason is not null)
                return false;
            s_all = [.. s_all, plugin];
        }
        plugin.Register();
        return true;
    }

    /// <summary>An installed plugin was removed. Its settings stay known until Ferry is started again, and do nothing.</summary>
    public static void Remove(SitePlugin plugin)
    {
        lock (Changing)
            s_all = [.. s_all.Where(other => other != plugin)];
    }

    /// <summary>Which links can be downloaded, by the machine that downloads them.</summary>
    /// <param name="secrets">null where none are kept, as on a server's agent</param>
    /// <param name="sent">on a server's agent, the secrets the PC sent</param>
    public static SiteCatalog Catalog(HttpFetcher http, FerrySettings settings, SecretStore? secrets, IReadOnlyDictionary<string, string>? sent = null)
    {
        var context = new PluginContext(http, settings, secrets) { Sent = sent ?? NoneSent };
        return SiteCatalog.Standard(http, settings.BrowserUserAgent, Plugins.SelectMany(plugin => plugin.Entries(context)),
                                    where: context.IsAgent ? OnTheServer : null);
    }

    /// <summary>What turns links to many files into the files, as the plugins that are on know them (<see cref="Listers"/>).</summary>
    /// <param name="downloadsElsewhere">a server does the downloading, and this only hands it links</param>
    /// <param name="sent">on a server's agent, the secrets the PC sent</param>
    public static LinkExpander Expander(HttpFetcher http, FerrySettings settings, SecretStore? secrets, bool downloadsElsewhere,
                                        IReadOnlyDictionary<string, string>? sent = null) =>
        new(Listers(http, settings, secrets, downloadsElsewhere, sent));

    /// <summary>
    /// What knows links to many files, from the plugins that are on. Where a server downloads, only the plugins that
    /// must read here (<see cref="SitePlugin.ReadsListsHere"/>): the server reads the rest, as it reads everything else.
    /// </summary>
    /// <param name="downloadsElsewhere">a server does the downloading, and this only hands it links</param>
    /// <param name="sent">on a server's agent, the secrets the PC sent</param>
    public static IReadOnlyList<IFileLister> Listers(HttpFetcher http, FerrySettings settings, SecretStore? secrets, bool downloadsElsewhere,
                                                     IReadOnlyDictionary<string, string>? sent = null)
    {
        var context = new PluginContext(http, settings, secrets, downloadsElsewhere) { Sent = sent ?? NoneSent };
        return [.. Plugins.Where(plugin => plugin.IsOn(settings) && (!downloadsElsewhere || plugin.ReadsListsHere))
                          .SelectMany(plugin => plugin.Listers(context))];
    }

    /// <summary>How files are downloaded here, with the user's rules for where they go, once those are on.</summary>
    public static Core.Download.DownloadOptions Download(FerrySettings settings) =>
        settings.Download with { Place = AfterDownloadPlugin.PlaceFor(settings) };
}
