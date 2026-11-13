using System.Diagnostics.CodeAnalysis;
using Ferry.Core;
using Ferry.Core.Browse;
using Ferry.Core.Http;
using Ferry.Core.Library;
using Ferry.Core.Plugins;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Host;

/// <summary>A package the server has, and what came of loading it.</summary>
/// <param name="Id">the plugin's id; the file's name when the package couldn't be read that far</param>
/// <param name="Sha256">of the package file, which tells one build of a plugin from another</param>
/// <param name="Plugin">what to run; null when it couldn't be loaded</param>
/// <param name="Problem">why it couldn't be loaded, for the person who installed it</param>
/// <param name="Browsing">its site to browse, when it has one</param>
/// <param name="Lookup">what searches its site for titles, when it does that; a throw inside it is said of the plugin</param>
public sealed record InstalledPlugin(string Id, string Version, string Sha256, SitePlugin? Plugin, string? Problem, SiteBrowsing? Browsing = null,
                                     Func<PluginContext, ITitleLookup>? Lookup = null);

/// <summary>A plugin's site as the Browser page needs it: which links are its pages, and what reads them.</summary>
/// <param name="Make">what reads the site, with the settings in the context; a throw inside it is said of the plugin</param>
public sealed record SiteBrowsing(LinkClaims Claims, PagePatterns Pages, Func<PluginContext, ISiteBrowsing> Make);

/// <summary>
/// The packages in a folder, loaded: each "*.ferryplugin" there is unpacked once, under its hash, and run from there. One
/// that can't be loaded says why and leaves the rest alone, and so does one that fails later, while it runs.
/// </summary>
public static class InstalledPlugins
{
    private const string UnpackedFolder = "unpacked";

    /// <param name="log">where a plugin's failures are written</param>
    public static IReadOnlyList<InstalledPlugin> Load(string folder, Action<string> log)
    {
        if (!Directory.Exists(folder))
            return [];
        var unpacked = Path.Combine(folder, UnpackedFolder);
        var plugins = new List<InstalledPlugin>();
        foreach (var file in Directory.EnumerateFiles(folder, "*" + PluginPackage.Extension).Order(StringComparer.Ordinal))
        {
            var plugin = Load(file, unpacked, plugins, log);
            log(plugin.Problem is null ? $"plugin {plugin.Id} {plugin.Version} loaded" : $"plugin {plugin.Id} not loaded: {plugin.Problem}");
            plugins.Add(plugin);
        }
        Forget(unpacked, plugins);
        return plugins;
    }

    /// <param name="before">the packages read before this one: a second package of a plugin is refused before any of it runs</param>
    private static InstalledPlugin Load(string file, string unpacked, List<InstalledPlugin> before, Action<string> log)
    {
        PluginPackage package;
        try
        {
            package = PluginPackage.Read(file);
        }
        catch (PluginException e)
        {
            return new InstalledPlugin(Path.GetFileNameWithoutExtension(file), "", "", null, e.Message);
        }
        var manifest = package.Manifest;
        if (before.Any(other => other.Id == manifest.Id))
            return new InstalledPlugin(manifest.Id, manifest.Version, package.Sha256, null, $"{Path.GetFileName(file)} is a second package of {manifest.Id}; only one can be installed.");
        try
        {
            var folder = Path.Combine(unpacked, package.Sha256);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(unpacked);
                PluginPackage.Unpack(file, folder);
            }
            var loaded = PluginLoader.Load(folder, manifest);
            var (plugin, browsing) = (loaded.Plugin, loaded as IBrowsingPlugin);
            // the PC shows and switches it by its manifest: code that says otherwise would be run under another name
            if (plugin.Name != manifest.Name || plugin.Enabled.Name != manifest.Enabled.Name || browsing?.Pages != manifest.Pages
                || loaded is ILookupPlugin != manifest.LooksUpTitles)
                throw new PluginException("Its code and its manifest disagree about its name, its switch or what it offers. It needs packing again.");
            var site = browsing is null ? null : new SiteBrowsing(loaded.Claims, browsing.Pages, context => new GuardedBrowsing(browsing.Browse(context), plugin.Name));
            Func<PluginContext, ITitleLookup>? lookup = loaded is ILookupPlugin looks ? context => new GuardedLookup(looks.Lookup(context), plugin.Name) : null;
            return new InstalledPlugin(manifest.Id, manifest.Version, package.Sha256, Guarded(plugin, log), null, site, lookup);
        }
#pragma warning disable CA1031 // a stranger's code, run for the first time: whatever it throws is its own failure
        catch (Exception e)
#pragma warning restore CA1031
        {
            return new InstalledPlugin(manifest.Id, manifest.Version, package.Sha256, null, e.Message);
        }
    }

    /// <summary>What was unpacked for packages that are gone, replaced by another build, or not running, goes.</summary>
    private static void Forget(string unpacked, List<InstalledPlugin> plugins)
    {
        if (!Directory.Exists(unpacked))
            return;
        foreach (var folder in Directory.EnumerateDirectories(unpacked).Where(folder => !plugins.Any(plugin => plugin.Plugin is not null && plugin.Sha256 == Path.GetFileName(folder))))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // in use, or not ours to delete: it is tried again at the next start
            }
        }
    }

    /// <summary>The plugin with everything it gives wrapped, so that a throw inside it is said of it and goes no further.</summary>
    private static SitePlugin Guarded(SitePlugin plugin, Action<string> log) => new(plugin.Name, plugin.Enabled)
    {
        About = plugin.About,
        SiteIds = plugin.SiteIds,
        Sites = context => Try(plugin.Name, log, () => plugin.Sites(context).Select(ISite (site) => new GuardedSite(site, plugin.Name, log)).ToList()),
        Listers = context => Try(plugin.Name, log, () => plugin.Listers(context).Select(IFileLister (lister) => new GuardedLister(lister, plugin.Name)).ToList()),
        ReadsListsHere = plugin.ReadsListsHere,
        KeepsLinksWhileOff = plugin.KeepsLinksWhileOff,
        Options = plugin.Options,
        Secrets = plugin.Secrets,
    };

    private static List<T> Try<T>(string plugin, Action<string> log, Func<List<T>> make)
    {
        try
        {
            return make();
        }
#pragma warning disable CA1031 // as above
        catch (Exception e)
#pragma warning restore CA1031
        {
            log($"plugin {plugin} failed making what it adds: {e.Message}");
            return [];
        }
    }
}

/// <summary>A plugin's site, asked through a wall: Ferry asks every site about every link, so one that throws would stop them all.</summary>
internal sealed class GuardedSite(ISite site, string plugin, Action<string> log) : ISite
{
    private bool _said;

    public string Id { get; } = site.Id;

    public string Name { get; } = site.Name;

    public bool Claims(Uri link)
    {
        try
        {
            return site.Claims(link);
        }
#pragma warning disable CA1031 // a stranger's code
        catch (Exception e)
#pragma warning restore CA1031
        {
            // once: it is asked for every link there is
            if (!_said)
                log($"plugin {plugin} failed telling whether a link is its own: {e.Message}");
            _said = true;
            return false;
        }
    }

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        try
        {
            return site.TryParseFile(link, out file, out reason);
        }
#pragma warning disable CA1031 // a stranger's code
        catch (Exception e)
#pragma warning restore CA1031
        {
            (file, reason) = (null, $"The {plugin} plugin failed on this link: {e.Message}");
            return false;
        }
    }

    public async Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        try
        {
            return await site.ResolveAsync(file, details, cancel);
        }
        catch (Exception e) when (e is not (StageException or OperationCanceledException))
        {
            throw new StageException(Stage.Resolve, $"The {plugin} plugin failed: {e.Message}", inner: e);
        }
    }
}

/// <summary>A plugin's reader of links to many files, asked through the same wall.</summary>
internal sealed class GuardedLister(IFileLister lister, string plugin) : IFileLister
{
    public async Task<FileList?> TryListAsync(Uri link, RequestDetails details, CancellationToken cancel)
    {
        try
        {
            return await lister.TryListAsync(link, details, cancel);
        }
        catch (Exception e) when (e is not (StageException or OperationCanceledException))
        {
            throw new StageException(Stage.Resolve, $"The {plugin} plugin failed: {e.Message}", inner: e);
        }
    }
}

/// <summary>A plugin's reader of its site, asked through the same wall.</summary>
internal sealed class GuardedBrowsing(ISiteBrowsing site, string plugin) : ISiteBrowsing
{
    public Task<BrowsedPage> OpenAsync(PageLink link, CancellationToken cancel) => Guard(() => site.OpenAsync(link, cancel));

    public Task<IReadOnlyList<AlbumCard>> AlbumsAsync(AlbumQuery query, CancellationToken cancel) => Guard(() => site.AlbumsAsync(query, cancel));

    public Task<byte[]> ThumbnailAsync(Uri picture, CancellationToken cancel) => Guard(() => site.ThumbnailAsync(picture, cancel));

    public Task<AlbumDetails> DetailsAsync(PageLink album, CancellationToken cancel) => Guard(() => site.DetailsAsync(album, cancel));

    private async Task<T> Guard<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception e) when (e is not (StageException or OperationCanceledException))
        {
            throw new StageException(Stage.Resolve, $"The {plugin} plugin failed: {e.Message}", inner: e);
        }
    }
}

/// <summary>A plugin's search of its site, asked through the same wall.</summary>
internal sealed class GuardedLookup(ITitleLookup lookup, string plugin) : ITitleLookup
{
    public async Task<IReadOnlyList<FoundTitle>> SearchAsync(string name, CancellationToken cancel)
    {
        try
        {
            return await lookup.SearchAsync(name, cancel);
        }
        catch (Exception e) when (e is not (StageException or OperationCanceledException))
        {
            throw new StageException(Stage.Resolve, $"The {plugin} plugin failed: {e.Message}", inner: e);
        }
    }
}
