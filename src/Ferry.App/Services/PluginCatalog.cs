using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.Official;
using Ferry.Plugins.Server;

namespace Ferry.App.Services;

/// <summary>A plugin as the Plugins page shows it.</summary>
/// <param name="Reads">what kinds of link or site it takes</param>
/// <param name="Limits">what it can't do yet, or what to expect</param>
/// <param name="Plugin">null for Server Support, whose page is its own</param>
public sealed record PluginCard(string Name, PluginGroup Group, string Glyph, string What, IReadOnlyList<string> Reads, IReadOnlyList<string> Limits, SitePlugin? Plugin);

/// <summary>
/// Every plugin there is a choice about, as a card: Server Support, whose page is its own, and the rest as they describe
/// themselves. Those that are part of Ferry (<see cref="SitePlugin.BuiltIn"/>) aren't cards: they share one page.
/// </summary>
public static class PluginCatalog
{
    public const string ServerSupport = "Server Support";

    /// <summary>The one entry for all that is part of Ferry.</summary>
    public const string BuiltIn = "Built in";

    public const string BuiltInGlyph = "";

    /// <summary>All of them as they are now, by group: one can be installed or removed while Ferry runs.</summary>
    public static IReadOnlyList<PluginCard> All => Build();

    /// <summary>What reads links without being asked to, in the order Ferry asks them.</summary>
    public static IReadOnlyList<SitePlugin> BuiltIns => [.. OfficialSites.Plugins.Where(plugin => plugin.BuiltIn)];

    /// <summary>The names of what reads links now: what is part of Ferry, and each plugin for links that is on.</summary>
    public static IEnumerable<string> LinkReaders(FerrySettings settings) =>
        OfficialSites.Plugins.Where(plugin => plugin.About.Group == PluginGroup.Links && plugin.IsOn(settings)).Select(plugin => plugin.Name);

    public static PluginCard? Find(string name) => All.FirstOrDefault(card => card.Name == name);

    public static bool IsOn(PluginCard card, FerrySettings settings) => card.Name switch
    {
        ServerSupport => settings.ServerSupportIsOn,
        _ => card.Plugin?.IsOn(settings) == true,
    };

    public static int CountOn(FerrySettings settings) => All.Count(card => IsOn(card, settings));

    private static List<PluginCard> Build()
    {
        var cards = new List<PluginCard>
        {
            new(ServerSupport, PluginGroup.Where, "\uE839", "Download on your own Linux server, even while this PC is off. Finished files come here over SSH.", [], [], null),
        };
        cards.AddRange(OfficialSites.Plugins.Where(plugin => !plugin.BuiltIn).Select(plugin =>
            new PluginCard(plugin.Name, plugin.About.Group, plugin.About.Glyph, plugin.Enabled.Help, plugin.About.Reads, plugin.About.Limits, plugin)));
        return [.. cards.OrderBy(card => card.Group)];
    }
}
