using Ferry.Core.Plugins;
using Ferry.Core.Sites;
using Ferry.Plugins.Official;

namespace Ferry.App.Services;

/// <summary>A plugin someone installed: its package, and what stands for it on this PC.</summary>
public sealed record InstalledPlugin(StoredPlugin Stored, SitePlugin Plugin);

/// <summary>
/// The plugins installed on this PC, among those that come with Ferry. None of their code runs here: each is shown,
/// switched and set from its manifest, and the server gets its package when Ferry connects.
/// </summary>
public sealed class InstalledPlugins(PluginStore store)
{
    private readonly List<InstalledPlugin> _plugins = [];

    public PluginStore Store => store;

    public IReadOnlyList<InstalledPlugin> All => _plugins;

    /// <summary>What was kept but can't be used, each with its reason, from when Ferry started.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>The packages kept from before, each among the plugins: as Ferry starts, before any settings are read.</summary>
    public void Load()
    {
        var problems = new List<string>();
        var kept = store.List(out var unread);
        foreach (var stored in kept)
        {
            var plugin = ServerOnlyPlugin.For(stored.Manifest);
            if (OfficialSites.TryAdd(plugin, out var reason))
                _plugins.Add(new InstalledPlugin(stored, plugin));
            else
                problems.Add($"{stored.Manifest.Name}: {reason}");
        }
        Problems = [.. unread, .. problems];
    }

    public InstalledPlugin? Find(SitePlugin plugin) => _plugins.FirstOrDefault(installed => installed.Plugin == plugin);

    public InstalledPlugin? Find(string id) => _plugins.FirstOrDefault(installed => installed.Stored.Manifest.Id == id);

    /// <summary>Keep <paramref name="package"/> and put it among the plugins, in place of the build of it installed before.</summary>
    /// <param name="source">the link it came from, where a newer one is looked for; null for a file</param>
    /// <exception cref="PluginException">it would take the place of another plugin; nothing changed</exception>
    /// <exception cref="IOException">it can't be kept; nothing changed</exception>
    public InstalledPlugin Install(PluginPackage package, string file, Uri? source = null)
    {
        var before = Find(package.Manifest.Id);
        if (before is not null)
            OfficialSites.Remove(before.Plugin);
        var plugin = ServerOnlyPlugin.For(package.Manifest);
        try
        {
            if (!OfficialSites.TryAdd(plugin, out var reason))
                throw new PluginException(reason);
            var installed = new InstalledPlugin(store.Add(package, file, source), plugin);
            _plugins.Remove(before!);
            _plugins.Add(installed);
            return installed;
        }
        catch
        {
            OfficialSites.Remove(plugin);
            if (before is not null)
                OfficialSites.TryAdd(before.Plugin, out _);
            throw;
        }
    }

    /// <exception cref="IOException">its package can't be deleted; nothing changed</exception>
    public void Remove(InstalledPlugin installed)
    {
        store.Remove(installed.Stored);
        OfficialSites.Remove(installed.Plugin);
        _plugins.Remove(installed);
    }
}
