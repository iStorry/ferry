namespace Ferry.Core.Sites;

/// <summary>What a plugin is for, on the Plugins page.</summary>
public enum PluginGroup
{
    /// <summary>Links it can read.</summary>
    Links,

    /// <summary>What it does around your downloads.</summary>
    Around,

    /// <summary>Where downloads run.</summary>
    Where,
}

/// <summary>How a plugin is shown. It has no UI of its own, as it also runs where there is none, so it says this much and the app draws it.</summary>
/// <param name="Glyph">its icon, a character of Segoe Fluent Icons</param>
/// <param name="Reads">what kinds of link or site it takes</param>
/// <param name="Limits">what it can't do yet, or what to expect</param>
public sealed record PluginAbout(PluginGroup Group, string Glyph, IReadOnlyList<string> Reads, IReadOnlyList<string> Limits)
{
    /// <summary>A reader of links that says nothing more of itself.</summary>
    public static PluginAbout Plain { get; } = new(PluginGroup.Links, "", [], []);
}
