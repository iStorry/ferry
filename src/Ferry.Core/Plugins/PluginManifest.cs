using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ferry.Core.Browse;
using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Core.Plugins;

/// <summary>A plugin's setting as its manifest carries it, so the PC can show and check it without the plugin's code.</summary>
/// <param name="MinSeconds">for a duration</param>
public sealed record ManifestSetting(string Name, SettingType Type, string Default, string Help, string Title, IReadOnlyList<string> Choices,
                                     long Min, long Max, double MinSeconds, double MaxSeconds, bool AppOnly)
{
    public static ManifestSetting From(SettingKey key) =>
        new(key.Name, key.Type, key.Default, key.Help, key.Title, key.Choices, key.Min, key.Max,
            key.MinDuration.TotalSeconds, key.MaxDuration.TotalSeconds, key.AppOnly);

    public SettingKey ToKey() => new(Name, Type, Default, Help, AppOnly: AppOnly)
    {
        Title = Title,
        Choices = Choices,
        Min = Min,
        Max = Max,
        MinDuration = TimeSpan.FromSeconds(MinSeconds),
        MaxDuration = TimeSpan.FromSeconds(MaxSeconds),
    };
}

/// <summary>
/// "plugin.json" in a package: all the PC needs of a plugin. Its card and page (<see cref="Name"/>, <see cref="What"/>,
/// <see cref="About"/>), the links that are its own, its switch, settings and secrets, and for the server, where its code is.
/// </summary>
/// <param name="Api">the <see cref="PluginApi.Version"/> it was built against</param>
/// <param name="Entry">the file in the package's lib folder with its code</param>
/// <param name="Type">the <see cref="IFerryPlugin"/> in <paramref name="Entry"/>, by its full name</param>
/// <param name="Enabled">its switch</param>
/// <param name="SiteIds">the ids of its sites, which the files it downloads carry</param>
/// <param name="Pages">which of its links are pages to browse; null when it has none</param>
/// <param name="LooksUpTitles">it can say which versions of a title its site lists, for the Library page</param>
/// <param name="Update">where a newer package is looked for, if anywhere</param>
public sealed partial record PluginManifest(string Id, string Name, string Version, int Api, string Entry, string Type, string Publisher,
                                            string What, PluginAbout About, LinkClaims Claims, ManifestSetting Enabled,
                                            IReadOnlyList<ManifestSetting> Settings, IReadOnlyList<PluginSecret> Secrets, IReadOnlyList<string> SiteIds,
                                            PagePatterns? Pages = null, bool LooksUpTitles = false, Uri? Update = null)
{
    public const string FileName = "plugin.json";

    private const int MaxName = 60;
    private const int MaxId = 64;

    /// <summary>The manifest of <paramref name="plugin"/>, for packing it.</summary>
    /// <param name="type">the plugin's type, by its full name</param>
    public static PluginManifest From(IFerryPlugin plugin, string version, string entry, string type, Uri? update = null)
    {
        var site = plugin.Plugin;
        return new PluginManifest(plugin.Id, site.Name, version, PluginApi.Version, entry, type, plugin.Publisher, site.Enabled.Help, site.About,
                                  plugin.Claims, ManifestSetting.From(site.Enabled), [.. site.Options.Select(ManifestSetting.From)], site.Secrets, site.SiteIds,
                                  (plugin as IBrowsingPlugin)?.Pages, plugin is ILookupPlugin, update);
    }

    public string ToJson() => JsonSerializer.Serialize(this, PluginJson.Default.PluginManifest);

    /// <summary>A manifest that can be used as it is; false, with the reason for the person installing it, when it can't.</summary>
    public static bool TryRead(string json, [NotNullWhen(true)] out PluginManifest? manifest, [NotNullWhen(false)] out string? reason)
    {
        try
        {
            manifest = JsonSerializer.Deserialize(json, PluginJson.Default.PluginManifest);
        }
        catch (JsonException e)
        {
            manifest = null;
            reason = $"Its {FileName} can't be read: {e.Message}";
            return false;
        }
        reason = manifest is null ? $"Its {FileName} is empty." : manifest.Problem();
        if (reason is not null)
            manifest = null;
        return manifest is not null;
    }

    /// <summary>Why this manifest can't be used, or null when it can.</summary>
    private string? Problem()
    {
        if (Api != PluginApi.Version)
            return $"It was made for plugin API {Api}, and this Ferry has API {PluginApi.Version}. It needs a build for this one.";
        if (Id.Length > MaxId || !IdForm().IsMatch(Id))
            return $"Its id, \"{Id}\", isn't one: lower-case letters and digits, with dots or dashes between them.";
        if (!IsLine(Name, MaxName) || !IsLine(Publisher, MaxName))
            return "Its name or publisher is missing, too long, or more than a line.";
        if (!System.Version.TryParse(Version, out _))
            return $"Its version, \"{Version}\", isn't one such as 1.2.0.";
        if (Path.GetFileName(Entry) != Entry || !Entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || Type.Length == 0)
            return "It doesn't say where its code is.";
        if (Update is not null && (!Update.IsAbsoluteUri || Update.Scheme != Uri.UriSchemeHttps))
            return "Its update address isn't an https link.";
        if (Enabled.Type != SettingType.Boolean)
            return "Its switch isn't an on or off setting.";
        var names = Settings.Prepend(Enabled).Select(setting => setting.Name).ToList();
        if (names.FirstOrDefault(name => !SettingForm().IsMatch(name)) is { } bad)
            return $"Its setting \"{bad}\" isn't named as a setting is ({SettingKey.Prefix}SOMETHING).";
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            return "Two of its settings have the same name.";
        if (Secrets.FirstOrDefault(secret => !SecretForm().IsMatch(secret.Name)) is { } secret)
            return $"Its secret \"{secret.Name}\" isn't named as one is: lower-case letters, digits and dashes.";
        if (Claims.Hosts.FirstOrDefault(host => Uri.CheckHostName(host) != UriHostNameType.Dns) is { } host)
            return $"\"{host}\" isn't a host's name.";
        if (Claims.HostsSetting is { } hosts && !Settings.Any(setting => setting.Name == hosts && setting.Type == SettingType.Hosts))
            return $"It says its hosts are in {hosts}, which isn't a setting of its own for hosts.";
        if (SiteIds.FirstOrDefault(id => !IdForm().IsMatch(id)) is { } site)
            return $"Its site's id, \"{site}\", isn't one: lower-case letters and digits, with dots or dashes between them.";
        if (Pages?.Problem() is { } pages)
            return pages;
        // every web link would be its own, and nothing else could be downloaded
        if (Claims.Schemes.FirstOrDefault(scheme => !Uri.CheckSchemeName(scheme) || scheme is "http" or "https" or "file") is { } scheme)
            return $"It can't take every \"{scheme}\" link.";
        return null;
    }

    private static bool IsLine(string text, int max) => text.Length is > 0 && text.Length <= max && !text.Any(char.IsControl);

    [GeneratedRegex(@"^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex IdForm();

    [GeneratedRegex(@"^FERRY_[A-Z0-9]+(_[A-Z0-9]+)*$")]
    private static partial Regex SettingForm();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SecretForm();
}

/// <summary>Source-generated JSON for a plugin's manifest and signature. What a manifest must have, it must have: one without is refused.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginSignature))]
internal sealed partial class PluginJson : JsonSerializerContext;
