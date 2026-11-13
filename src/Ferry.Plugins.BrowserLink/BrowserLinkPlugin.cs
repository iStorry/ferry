using System.Runtime.Versioning;
using System.Text.Json;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Microsoft.Win32;

namespace Ferry.Plugins.BrowserLink;

/// <summary>
/// What the Browser Link plugin adds to Ferry: "Download with Ferry" on a link in Chrome or Edge, with the browser's cookies
/// for that link's site. The extension talks to the CLI (<see cref="BrowserHost"/>), which hands the link to the app.
/// </summary>
public static class BrowserLinkPlugin
{
    public const string HostName = "com.ferry.browser_link";

    /// <summary>The ID that the key in the extension's manifest gives it, for a copy loaded by hand.</summary>
    public const string ExtensionId = "lpedaegeepflbpcifaeiaepkljcfiaek";

    /// <summary>
    /// The IDs the Chrome and Edge stores give the extension. Empty, and so without effect, until it is published there
    /// (docs/extension-store.md): the store IDs go here then, and nothing else changes.
    /// </summary>
    public static IReadOnlyList<string> StoreExtensionIds { get; } = [];

    /// <summary>Every ID the app takes links from.</summary>
    public static IEnumerable<string> ExtensionIds => StoreExtensionIds.Prepend(ExtensionId);

    /// <summary>A handoff the app never took (it didn't start, or was closed first) is swept this long after it was written.</summary>
    private static readonly TimeSpan KeepHandoffs = TimeSpan.FromDays(1);

    private static readonly string[] BrowserKeys = [@"Software\Google\Chrome\NativeMessagingHosts\", @"Software\Microsoft\Edge\NativeMessagingHosts\"];

    /// <summary>Off on a new install: it means setting up an extension in the browser.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_BROWSER_LINK", SettingType.Boolean, "false",
        "\"Download with Ferry\" on a link in Chrome or Edge, with the browser's cookies for that link's site.")
    { Title = "Browser Link" };

    public static SitePlugin Plugin { get; } = new("Browser Link", Enabled)
    {
        About = new(PluginGroup.Around, "\uE774", ["Chrome", "Edge"], []),
    };

    /// <summary>Where a link from the browser waits for the app. Encrypted for this user, with cookies among it, so a shared place is fine; the app takes it at once.</summary>
    public static SecretStore Handoffs => new(Path.Combine(Path.GetTempPath(), "ferry-browser-link"));

    /// <summary>Lets Chrome and Edge start the CLI for the extension: for this user only, nothing system-wide.</summary>
    /// <param name="folder">where to keep the description they read</param>
    /// <param name="cli">the CLI's program</param>
    [SupportedOSPlatform("windows")]
    public static void Install(string folder, string cli)
    {
        Directory.CreateDirectory(folder);
        var manifest = Path.Combine(folder, $"{HostName}.json");
        using (var file = File.Create(manifest))
        using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("name", HostName);
            json.WriteString("description", "Ferry: downloads from the browser");
            json.WriteString("path", cli);
            json.WriteString("type", "stdio");
            json.WriteStartArray("allowed_origins");
            foreach (var id in ExtensionIds)
                json.WriteStringValue($"chrome-extension://{id}/");
            json.WriteEndArray();
            json.WriteEndObject();
        }
        foreach (var key in BrowserKeys)
        {
            using var host = Registry.CurrentUser.CreateSubKey(key + HostName);
            host.SetValue(null, manifest);
        }
    }

    [SupportedOSPlatform("windows")]
    public static void Uninstall()
    {
        foreach (var key in BrowserKeys)
            Registry.CurrentUser.DeleteSubKeyTree(key + HostName, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// Remove handoffs the app never took, older than <see cref="KeepHandoffs"/>: they hold the browser's cookies, encrypted
    /// for this user, and have no business staying on disk. Called as a new one is written.
    /// </summary>
    public static void SweepHandoffs()
    {
        var folder = Handoffs.Folder;
        if (!Directory.Exists(folder))
            return;
        var before = DateTime.UtcNow - KeepHandoffs;
        foreach (var file in Directory.EnumerateFiles(folder).Where(file => File.GetLastWriteTimeUtc(file) < before))
            File.Delete(file);
    }

    /// <summary>What the browser handed over under <paramref name="id"/>, which is then gone; null when there is none.</summary>
    public static (string Link, RequestDetails Details)? TakeHandoff(string id)
    {
        var store = Handoffs;
        var bytes = store.Load(id);
        store.Delete(id);
        if (bytes is null)
            return null;
        try
        {
            return BrowserHost.ReadMessage(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
