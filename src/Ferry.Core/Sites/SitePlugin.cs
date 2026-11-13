using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Settings;

namespace Ferry.Core.Sites;

/// <summary>What a plugin has to work with when it makes what it adds.</summary>
/// <param name="Secrets">null where none are kept, as on a server's agent: a plugin that needs one adds nothing there</param>
/// <param name="DownloadsElsewhere">this machine only hands links on, as a PC does in server mode</param>
public sealed record PluginContext(HttpFetcher Http, FerrySettings Settings, SecretStore? Secrets, bool DownloadsElsewhere = false)
{
    public string UserAgent => Settings.BrowserUserAgent;

    /// <summary>This is a server's agent, whose finished files go on to a PC one file at a time.</summary>
    public bool IsAgent => Secrets is null;

    /// <summary>
    /// On a server's agent, the secrets the PC sent for the plugins installed there, by name. They came over SSH and are kept
    /// in memory only, so an agent that started again has none until the PC is back.
    /// </summary>
    public IReadOnlyDictionary<string, string> Sent { get; init; } = new Dictionary<string, string>();

    /// <summary>A secret as text, or null when there is none to use.</summary>
    public string? Secret(PluginSecret secret) =>
        Secrets is null ? Sent.GetValueOrDefault(secret.Name) is { Length: > 0 } sent ? sent : null
            : Secrets.Load(secret.Name) is { Length: > 0 } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
}

/// <summary>A login or key a plugin needs, kept in the <see cref="SecretStore"/> and never in the settings.</summary>
/// <param name="Name">what it is saved under</param>
/// <param name="Title">what people call it</param>
public sealed record PluginSecret(string Name, string Title, string Help);

/// <summary>A plugin with no page of its own design: being on or off, some settings and secrets, and what it adds to Ferry.</summary>
/// <param name="enabled">its switch</param>
public sealed class SitePlugin(string name, SettingKey enabled)
{
    public string Name => name;

    public SettingKey Enabled => enabled;

    /// <summary>For its card and its page.</summary>
    public PluginAbout About { get; init; } = PluginAbout.Plain;

    /// <summary>The <see cref="ISite.Id"/> of each of its sites.</summary>
    public IReadOnlyList<string> SiteIds { get; init; } = [];

    /// <summary>Its sites, on or off.</summary>
    public Func<PluginContext, IEnumerable<ISite>> Sites { get; init; } = _ => [];

    /// <summary>What it knows of links to many files, while it is on.</summary>
    public Func<PluginContext, IEnumerable<IFileLister>> Listers { get; init; } = _ => [];

    /// <summary>
    /// Its listers run on this PC even when a server downloads, because they need what only the PC has (a key kept
    /// here). Otherwise a server reads links to many files itself, as it reads everything else.
    /// </summary>
    public bool ReadsListsHere { get; init; }

    /// <summary>Off, its links are still known as its own, and refused. Otherwise they go on to whoever takes them next, as they would without it.</summary>
    public bool KeepsLinksWhileOff { get; init; } = true;

    /// <summary>
    /// It only makes ordinary links work and asks nothing of the user, so it is on from the start and the app shows it
    /// as part of Ferry, with no switch of its own. Its setting still turns it off, from the file or the environment.
    /// </summary>
    public bool BuiltIn { get; init; }

    /// <summary>For its page.</summary>
    public IReadOnlyList<SettingKey> Options { get; init; } = [];

    /// <summary>For its page.</summary>
    public IReadOnlyList<PluginSecret> Secrets { get; init; } = [];

    public bool Owns(string siteId) => SiteIds.Contains(siteId);

    public bool IsOn(FerrySettings settings) => settings.Boolean(enabled);

    /// <summary>Its sites, as the settings in effect say whether to use them.</summary>
    public IEnumerable<SiteEntry> Entries(PluginContext context) =>
        IsOn(context.Settings) || KeepsLinksWhileOff
            ? Sites(context).Select(site => new SiteEntry(site, IsOn(context.Settings)))
            : [];

    public void Register() => SettingKey.Contribute([enabled, .. Options]);
}
