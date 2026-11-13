using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Feeds;

/// <summary>What the Feeds plugin adds to Ferry: new files on the RSS and Atom feeds you list are added by themselves.</summary>
public static class FeedsPlugin
{
    /// <summary>Off on a new install: it fetches and downloads without being asked each time, so that has to be chosen.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_FEEDS", SettingType.Boolean, "false",
        "New files on the RSS and Atom feeds you list are added by themselves.")
    { Title = "Feeds" };

    public static readonly SettingKey Urls = new("FERRY_FEEDS", SettingType.Text, "",
        "The feeds to watch, separated by spaces. What is on a feed when it is first read counts as seen, and isn't added.")
    { Title = "Feeds to watch" };

    public static readonly SettingKey Interval = new("FERRY_FEED_INTERVAL", SettingType.Duration, "1h",
        "How long to wait between reading the feeds.")
    {
        Title = "Read them every",
        MinDuration = TimeSpan.FromMinutes(5),
    };

    public static readonly SettingKey MostAtOnce = new("FERRY_FEED_MOST", SettingType.WholeNumber, "20",
        "The most files one reading of the feeds adds, so a feed can't fill the queue. The rest wait for the next.")
    {
        Title = "Most files to add at once",
        Min = 1,
        Max = 500,
    };

    public static SitePlugin Plugin { get; } = new("Feeds", Enabled)
    {
        About = new(PluginGroup.Around, "\uE789", ["RSS", "Atom"], ["Keeps hashes of what it saw, not the links."]),
        Options = [Urls, Interval, MostAtOnce],
    };
}
