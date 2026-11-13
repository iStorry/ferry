using System.Diagnostics.CodeAnalysis;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Core.Plugins;

/// <summary>A package kept on this PC.</summary>
/// <param name="File">where it is kept</param>
public sealed record StoredPlugin(PluginPackage Package, string File)
{
    public PluginManifest Manifest => Package.Manifest;

    /// <summary>What it is called on the server, where a plugin's package is found by its id.</summary>
    public string ServerName => Manifest.Id + PluginPackage.Extension;
}

/// <summary>
/// The plugins installed on this PC, each as its package, "{id}.ferryplugin", in one folder. The PC runs none of them: it
/// keeps them to show them, and to put them on the server.
/// </summary>
public sealed class PluginStore(string folder)
{
    /// <summary>Every package that can be read, by name; one that can't is left where it is and said in <paramref name="problems"/>.</summary>
    public IReadOnlyList<StoredPlugin> List(out IReadOnlyList<string> problems)
    {
        var (plugins, unread) = (new List<StoredPlugin>(), new List<string>());
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*" + PluginPackage.Extension).Order(StringComparer.Ordinal))
            {
                try
                {
                    plugins.Add(new StoredPlugin(PluginPackage.Read(file), file));
                }
                catch (PluginException e)
                {
                    unread.Add($"{Path.GetFileName(file)}: {e.Message}");
                }
            }
        }
        problems = unread;
        return [.. plugins.OrderBy(plugin => plugin.Manifest.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Keep <paramref name="package"/>, read from <paramref name="file"/>, in place of any package of the same plugin.</summary>
    /// <exception cref="IOException">it can't be written</exception>
    public StoredPlugin Add(PluginPackage package, string file)
    {
        Directory.CreateDirectory(folder);
        var kept = Path.Combine(folder, package.Manifest.Id + PluginPackage.Extension);
        AtomicFile.Write(kept, File.ReadAllBytes(file));
        return new StoredPlugin(package, kept);
    }

    /// <exception cref="IOException">it can't be deleted</exception>
    public void Remove(StoredPlugin plugin) => File.Delete(Path.Combine(folder, plugin.ServerName));
}

/// <summary>An installed plugin as this PC knows it, from its manifest alone.</summary>
public static class ServerOnlyPlugin
{
    /// <summary>
    /// The plugin to show and switch here: its name, settings and secrets are its own, and its one site only knows which
    /// links are the plugin's, so that none is taken for a plain web page. The server has the plugin that reads them.
    /// </summary>
    public static SitePlugin For(PluginManifest manifest)
    {
        // the first takes every link of the plugin: which of its sites a link is on only the plugin can tell
        IReadOnlyList<string> ids = manifest.SiteIds.Count > 0 ? manifest.SiteIds : ["installed:" + manifest.Id];
        return new SitePlugin(manifest.Name, manifest.Enabled.ToKey())
        {
            About = manifest.About,
            SiteIds = ids,
            Sites = context => ids.Select(ISite (id, index) => new ServerOnlySite(manifest, id, index == 0, context.Settings)),
            Options = [.. manifest.Settings.Select(setting => setting.ToKey())],
            Secrets = manifest.Secrets,
        };
    }
}

/// <summary>
/// Stands on this PC for the sites of a plugin that runs on the server. It knows which links are the plugin's, so the app
/// can show them as its own and hand them to the server, which reads them. Asked to download one here, it says where the
/// plugin runs.
/// </summary>
/// <param name="takesLinks">it is the one of the plugin's sites that stands for all of them when a link is asked about</param>
internal sealed class ServerOnlySite(PluginManifest manifest, string id, bool takesLinks, FerrySettings settings) : ISite
{
    public string Id => id;

    public string Name => manifest.Name;

    public bool Claims(Uri link) => takesLinks && manifest.Claims.Claims(link, settings);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        // which of its links are files only the plugin can tell: the server says so when it is given one
        file = new FileLink(Id, link, link.AbsoluteUri);
        reason = null;
        return true;
    }

    public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel) =>
        throw new StageException(Stage.Link, $"This is a {Name} link, and {Name} runs on your server, not on this PC. Turn on Server Support in Plugins and download on the server, then add it again.");
}
