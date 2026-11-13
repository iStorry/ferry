using Ferry.Core;
using Ferry.Core.Plugins;

namespace Ferry.Plugins.Server;

/// <summary>
/// Makes the server's plugins the ones this PC has installed: one run of ssh to see what is there, then only what differs
/// goes over or is removed. The PC decides: a package put on the server by hand, or by another PC, is removed.
/// </summary>
internal static class PluginSync
{
    /// <param name="root">the agent's folder on the server</param>
    /// <returns>whether anything changed there, so that an agent that is running has to start again to see it</returns>
    /// <exception cref="StageException">the server can't be reached, or what arrived isn't what was sent (<see cref="Stage.Server"/>)</exception>
    public static async Task<bool> SyncAsync(ServerConnection connection, string root, IReadOnlyList<StoredPlugin> plugins, CancellationToken cancel)
    {
        var folder = $"{root}/plugins";
        var quoted = AgentInstaller.Quote(folder);
        var there = Parse(await connection.RunAsync($"cd {quoted} 2>/dev/null && sha256sum -- *{PluginPackage.Extension} 2>/dev/null || true", cancel));
        var wanted = plugins.ToDictionary(plugin => plugin.ServerName, StringComparer.Ordinal);
        var missing = wanted.Values.Where(plugin => there.GetValueOrDefault(plugin.ServerName) != plugin.Package.Sha256).ToList();
        var extra = there.Keys.Where(name => !wanted.ContainsKey(name)).ToList();
        if (missing.Count == 0 && extra.Count == 0)
            return false;
        await connection.RunAsync($"mkdir -p {quoted} && chmod 700 {quoted}", cancel);
        foreach (var plugin in missing)
        {
            var (arriving, kept) = (AgentInstaller.Quote($"{folder}/{plugin.ServerName}.arriving"), AgentInstaller.Quote($"{folder}/{plugin.ServerName}"));
            await connection.UploadAsync(plugin.File, $"{folder}/{plugin.ServerName}.arriving", cancel);
            var arrived = await connection.RunAsync($"sha256sum {arriving}", cancel);
            if (!arrived.StartsWith(plugin.Package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await connection.RunAsync($"rm -f {arriving}", cancel);
                throw new StageException(Stage.Server, $"The {plugin.Manifest.Name} plugin didn't arrive on the server as it was sent.", isRetryable: true);
            }
            await connection.RunAsync($"mv -f {arriving} {kept}", cancel);
        }
        if (extra.Count > 0)
            await connection.RunAsync($"rm -f -- {string.Join(' ', extra.Select(name => AgentInstaller.Quote($"{folder}/{name}")))}", cancel);
        return true;
    }

    /// <summary>sha256sum's lines, "{hash}  {name}", by name.</summary>
    internal static Dictionary<string, string> Parse(string answer)
    {
        var there = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in answer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Split(' ', 2) is [{ Length: 64 } hash, var name])
                there[name.TrimStart(' ', '*')] = hash.ToLowerInvariant();
        }
        return there;
    }
}
