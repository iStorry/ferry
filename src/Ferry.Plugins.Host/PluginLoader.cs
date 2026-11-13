using System.Reflection;
using System.Runtime.Loader;
using Ferry.Core.Plugins;

namespace Ferry.Plugins.Host;

/// <summary>Runs an installed plugin's code. Only where code can be loaded at all: the server's agent, and the tool that packs plugins.</summary>
public static class PluginLoader
{
    /// <summary>The plugin unpacked in <paramref name="folder"/>, as its manifest says to find it.</summary>
    /// <exception cref="PluginException">its code can't be loaded, or isn't the plugin the manifest says</exception>
    public static IFerryPlugin Load(string folder, PluginManifest manifest)
    {
        var plugin = Create(Assembly(Path.Combine(folder, manifest.Entry)), manifest.Type);
        return plugin.Id == manifest.Id
            ? plugin
            : throw new PluginException($"Its code is \"{plugin.Id}\", not \"{manifest.Id}\" as its manifest says.");
    }

    /// <summary>The one plugin in an assembly that hasn't been packed yet, and its type's name for the manifest.</summary>
    /// <exception cref="PluginException">it can't be loaded, or has no plugin, or several</exception>
    public static (IFerryPlugin Plugin, string Type) Discover(string assemblyPath)
    {
        var assembly = Assembly(assemblyPath);
        var types = Guarded(() => assembly.GetExportedTypes().Where(type => type.IsClass && !type.IsAbstract && typeof(IFerryPlugin).IsAssignableFrom(type)).ToList());
        return types is [var only]
            ? (Create(assembly, only.FullName!), only.FullName!)
            : throw new PluginException($"{Path.GetFileName(assemblyPath)} has {types.Count} public types that are an {nameof(IFerryPlugin)}; a plugin has one.");
    }

    private static Assembly Assembly(string path) =>
        Guarded(() => new PluginLoadContext(Path.GetFullPath(path)).LoadFromAssemblyPath(Path.GetFullPath(path)));

    private static IFerryPlugin Create(Assembly assembly, string type) =>
        Guarded(() => assembly.GetType(type) is { } found && Activator.CreateInstance(found) is IFerryPlugin plugin
            ? plugin
            : throw new PluginException($"{type} isn't an {nameof(IFerryPlugin)} with a public constructor that takes nothing."));

    /// <summary>Whatever a plugin's code throws as it loads is the plugin's failure, said as one.</summary>
    private static T Guarded<T>(Func<T> load)
    {
        try
        {
            return load();
        }
        catch (TargetInvocationException e)
        {
            throw new PluginException($"Its code failed as it started: {(e.InnerException ?? e).Message}", e);
        }
#pragma warning disable CA1031 // a stranger's code: any failure of it is reported against it, and the host carries on
        catch (Exception e) when (e is not PluginException)
#pragma warning restore CA1031
        {
            throw new PluginException($"Its code can't be loaded: {e.Message}", e);
        }
    }
}

/// <summary>A plugin's own assemblies, from its folder. Whatever Ferry already has is Ferry's, or the plugin's <c>ISite</c> would be another type than Ferry's.</summary>
internal sealed class PluginLoadContext(string entry) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(entry))
{
    private readonly string _folder = Path.GetDirectoryName(entry)!;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (Default.Assemblies.Any(loaded => loaded.GetName().Name == assemblyName.Name))
            return null;
        var path = Path.Combine(_folder, assemblyName.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}
