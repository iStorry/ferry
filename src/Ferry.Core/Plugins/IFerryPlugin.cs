using Ferry.Core.Browse;
using Ferry.Core.Library;
using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Core.Plugins;

/// <summary>The contract between Ferry and the plugins people install, as a number a package states and Ferry checks.</summary>
public static class PluginApi
{
    /// <summary>Raised by any change that would break a plugin built against the one before.</summary>
    public const int Version = 1;
}

/// <summary>
/// What an installed plugin gives Ferry. Its assembly has one public type of this, with a public constructor that takes
/// nothing. The plugin runs on the server only: the PC knows it by its manifest, which is written from this when it is packed.
/// </summary>
public interface IFerryPlugin
{
    /// <summary>Its own name among all plugins, never shown: "acme.example".</summary>
    string Id { get; }

    /// <summary>Who made it, as people know them.</summary>
    string Publisher { get; }

    /// <summary>Its name, switch, settings, secrets and sites, as a plugin that comes with Ferry has them.</summary>
    SitePlugin Plugin { get; }

    /// <summary>The links that are its own, said so that the PC can tell without running it.</summary>
    LinkClaims Claims { get; }
}

/// <summary>A plugin whose site has albums to look through: the Browser page shows them, and the server watches them for new files.</summary>
public interface IBrowsingPlugin : IFerryPlugin
{
    /// <summary>Which of its links are an album's page and which a file's.</summary>
    PagePatterns Pages { get; }

    /// <summary>What reads its pages and its list of albums, with the settings as they are now.</summary>
    ISiteBrowsing Browse(PluginContext context);
}

/// <summary>A plugin whose site lists titles with their versions: the Library page asks it whether what is in a folder of yours is the newest.</summary>
public interface ILookupPlugin : IFerryPlugin
{
    /// <summary>What searches its site, with the settings as they are now.</summary>
    ITitleLookup Lookup(PluginContext context);
}

/// <summary>Searches a site for a title.</summary>
public interface ITitleLookup
{
    /// <summary>What the site lists for <paramref name="name"/>, newest first.</summary>
    /// <exception cref="StageException">the site couldn't be asked, or isn't set up</exception>
    Task<IReadOnlyList<FoundTitle>> SearchAsync(string name, CancellationToken cancel);
}

/// <summary>Reads a site's pages and its list of albums.</summary>
public interface ISiteBrowsing : IPageBrowser, IAlbumDirectory
{
}

/// <summary>Which links belong to a plugin.</summary>
/// <param name="Hosts">web links on these hosts and any host under them ("cdn.example.com" is under "example.com")</param>
/// <param name="Schemes">links of these kinds, other than web links ("magnet")</param>
/// <param name="HostsSetting">a setting of the plugin's, of hosts, whose hosts are its own too: for a site whose addresses change</param>
public sealed record LinkClaims(IReadOnlyList<string> Hosts, IReadOnlyList<string> Schemes, string? HostsSetting = null)
{
    public static LinkClaims None { get; } = new([], []);

    /// <param name="settings">where <see cref="HostsSetting"/> is read; without them only <see cref="Hosts"/> count</param>
    public bool Claims(Uri link, FerrySettings? settings = null) =>
        LinkProbe.IsWebLink(link)
            ? AllHosts(settings).Any(host => link.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || link.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
            : Schemes.Contains(link.Scheme, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<string> AllHosts(FerrySettings? settings) =>
        HostsSetting is { } name && settings is not null && SettingKey.TryFind(name, out var key)
            ? Hosts.Concat(settings.Text(key).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : Hosts;
}

/// <summary>A plugin's package can't be used; the message says why, for the person installing it.</summary>
public sealed class PluginException(string message, Exception? inner = null) : Exception(message, inner);
