using Ferry.Core.Sites;
using Ferry.Plugins.Host;
using Ferry.Plugins.Official;
using Ferry.Plugins.Server;

namespace Ferry.Agent;

/// <summary>The plugins installed on this server, loaded as the agent starts. Only in the server's build: nothing else can load code.</summary>
internal static class ServerPlugins
{
    /// <summary>Load every package in the agent's plugins folder, and add those that can be to what the agent downloads with.</summary>
    /// <returns>each package, loaded or not, for the PC to hear of; the first of them with a site to browse, and the first that looks titles up, if any</returns>
    public static (List<AgentPlugin> Plugins, AgentBrowsing? Browsing, AgentLookup? Lookup) Load(AgentPaths paths)
    {
        var plugins = new List<AgentPlugin>();
        AgentBrowsing? browsing = null;
        AgentLookup? lookup = null;
        foreach (var installed in InstalledPlugins.Load(paths.PluginsFolder, AgentHost.Log))
        {
            var problem = installed.Problem;
            if (installed.Plugin is { } plugin)
            {
                if (!OfficialSites.TryAdd(plugin, out problem))
                    AgentHost.Log($"plugin {installed.Id} not used: {problem}");
                else
                {
                    if (installed.Browsing is { } site && browsing is null)
                        browsing = Browsing(plugin, site);
                    else if (installed.Browsing is not null)
                        AgentHost.Log($"plugin {installed.Id} has a site to browse, and {browsing!.Name}'s is browsed already: only one is, for now");
                    if (installed.Lookup is { } make && lookup is null)
                        lookup = new AgentLookup(plugin.Name, plugin.IsOn, make);
                    else if (installed.Lookup is not null)
                        AgentHost.Log($"plugin {installed.Id} looks titles up, and {lookup!.Name} does already: only one does, for now");
                }
            }
            plugins.Add(new AgentPlugin(installed.Id, installed.Version, installed.Sha256, problem));
        }
        return (plugins, browsing, lookup);
    }

    private static AgentBrowsing Browsing(SitePlugin plugin, SiteBrowsing site) =>
        new(plugin.Name, plugin.IsOn,
            (text, settings) => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var link) && site.Claims.Claims(link, settings) && site.Pages.TryRead(link, out var page) ? page : null,
            site.Make);
}
