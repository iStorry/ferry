using Ferry.Core.Http;
using Ferry.Core.Updates;
using Ferry.Core;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Ferry.Plugins.Server;

/// <summary>
/// Puts the agent on the server and starts it. The agent is the Linux build of the ferry CLI, uploaded to
/// ~/.ferry/bin/ferry-{hash} (checked by SHA-256 before it is made executable), with ~/.ferry/bin/current pointing at
/// it. No root, no package, no open port.
/// </summary>
public sealed class AgentInstaller(ServerConnection connection)
{
    private const string Current = "current";

    // everything a connect needs to know, in one run of ssh: each costs a full login (2.7 s on a far server)
    // on one line, as csh won't take a line break inside quotes
    private const string ProbeScript =
        """printf 'home=%s\n' "$HOME"; """ +
        """printf 'uname=%s\n' "$(uname -sm)"; """ +
        """if ls /lib/ld-musl-* >/dev/null 2>&1; then echo musl=yes; fi; """ +
        "b=\"" + AgentPaths.ShellRoot + "/" + AgentPaths.BinFolderName + "\"; " +
        """if [ -x "$b/current" ]; then printf 'current=%s\n' "$(readlink "$b/current")"; printf 'sha256=%s\n' "$(sha256sum "$b/current" 2>/dev/null | cut -d' ' -f1)"; fi; """ +
        "pid=$(cat \"" + AgentPaths.ShellRoot + "/run/agent.pid\" 2>/dev/null); " +
        """if [ -n "$pid" ]; then printf 'running=%s\n' "$(readlink "/proc/$pid/exe" 2>/dev/null)"; fi; """ +
        """ps -u "$(id -u)" -o etimes=,args= 2>/dev/null | grep '[a]gent attach' | while read -r age rest; do echo "attach=$age"; done; """ +
        """true""";

    // an attach this young belongs to a Ferry that is connected (or reconnecting) right now, not one long gone
    private static readonly TimeSpan InUseWithin = TimeSpan.FromMinutes(2);

    // the agent builds hashed so far, by path: a reconnect doesn't read the whole build again while it is the same file
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Sha256)> Hashes = new(StringComparer.Ordinal);

    private AgentFacts? _facts;
    private string? _home;

    /// <summary>What the server is and which agent it has, from one run of ssh; asked once.</summary>
    public async Task<AgentFacts> ProbeAsync(CancellationToken cancel)
    {
        if (_facts is { } known)
            return known;
        var answer = await connection.RunAsync(ProbeScript, cancel);
        var facts = AgentFacts.Parse(answer);
        if (facts.Home.Length == 0)
            throw new StageException(Stage.Server, $"{connection.Display} has no home folder for the agent.");
        _home = facts.Home;
        return _facts = facts;
    }

    /// <summary>The agent as the server runs it: the build installed last.</summary>
    public async Task<string> CurrentAsync(CancellationToken cancel) => $"{await BinFolderAsync(cancel)}/{Current}";

    /// <summary>
    /// Refuse to replace an agent of another build while another Ferry uses it (an older copy on this PC, or another
    /// PC): replacing it would cut that one off, which would then replace this one's, and so on, each losing its
    /// connection every few seconds.
    /// </summary>
    /// <exception cref="StageException">another version is connected (<see cref="Stage.Server"/>, retryable: it may
    /// close or update)</exception>
    public async Task EnsureNotInUseByAnotherBuildAsync(string localBinary, CancellationToken cancel)
    {
        var facts = await ProbeAsync(cancel);
        var name = $"ferry-{(await HashAsync(localBinary, cancel))[..16]}";
        if (facts.RunsOtherThan(name) && facts.AttachAges.Any(age => age < InUseWithin))
        {
            throw new StageException(Stage.Server,
                $"Another Ferry of a different version is connected to {connection.Display} right now (an older copy on this PC, " +
                "or another PC). Close or update it, then this one connects: two versions would keep replacing each other's agent.",
                isRetryable: true);
        }
    }

    /// <summary>Whether an agent was installed before (so one can be used without a local build).</summary>
    public async Task<bool> IsInstalledAsync(CancellationToken cancel) => (await ProbeAsync(cancel)).CurrentName is not null;

    /// <summary>The server's runtime id ("linux-x64", "linux-arm64").</summary>
    /// <exception cref="StageException">the server is something the agent isn't built for</exception>
    public async Task<string> DetectAsync(CancellationToken cancel)
    {
        var facts = await ProbeAsync(cancel);
        return RidFor(facts.Uname, facts.Musl)
               ?? throw new StageException(Stage.Server,
                   $"{connection.Display} runs {(facts.Musl ? "a musl-based Linux (such as Alpine)" : facts.Uname.Length > 0 ? facts.Uname : "an unknown system")}; " +
                   "Ferry's agent needs Linux with glibc on x64 or arm64.");
    }

    /// <summary>"Linux x86_64" → "linux-x64", "Linux aarch64" → "linux-arm64"; null for anything else.</summary>
    public static string? RidFor(string uname, bool musl = false)
    {
        var parts = uname.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (musl || parts.Length < 2 || parts[0] != "Linux")
            return null;
        return parts[1] switch
        {
            "x86_64" or "amd64" => "linux-x64",
            "aarch64" or "arm64" => "linux-arm64",
            _ => null,
        };
    }

    /// <summary>Upload <paramref name="localBinary"/> unless that same build is installed, and make it the current one.</summary>
    /// <returns>whether an agent of another build is running, which must be stopped for this one to start: it may
    /// report the same version (two builds of the same commit), and asking the process itself also catches an earlier
    /// stop that failed</returns>
    public async Task<bool> InstallAsync(string localBinary, CancellationToken cancel)
    {
        var hash = await HashAsync(localBinary, cancel);
        var facts = await ProbeAsync(cancel);
        var bin = await BinFolderAsync(cancel);
        var name = $"ferry-{hash[..16]}";
        if (facts.CurrentName == name && string.Equals(facts.CurrentSha256, hash, StringComparison.OrdinalIgnoreCase))
            return facts.RunsOtherThan(name); // installed and current already: the usual connect, no more ssh runs
        _facts = null; // what the probe saw is about to change
        var installed = $"{bin}/{name}";
        var existing = await connection.RunAsync($"sha256sum {Quote(installed)} 2>/dev/null || true", cancel);
        if (!existing.StartsWith(hash, StringComparison.OrdinalIgnoreCase))
        {
            var root = bin[..bin.LastIndexOf('/')];
            await connection.RunAsync($"mkdir -p {Quote(bin)} && chmod 700 {Quote(root)} {Quote(bin)}", cancel);
            var temporary = installed + ".tmp";
            await connection.UploadAsync(localBinary, temporary, cancel);
            var uploaded = await connection.RunAsync($"sha256sum {Quote(temporary)}", cancel);
            if (!uploaded.StartsWith(hash, StringComparison.OrdinalIgnoreCase))
            {
                await connection.RunAsync($"rm -f {Quote(temporary)}", cancel);
                throw new StageException(Stage.Server, "The agent arrived on the server damaged (its SHA-256 doesn't match); try again.");
            }
            await connection.RunAsync($"chmod 700 {Quote(temporary)}", cancel);
            // run once before anything depends on it: a home that allows no programs, or a system too old, is said here and not as a lost connection
            try
            {
                await connection.RunAsync($"{Quote(temporary)} --version >/dev/null", cancel);
            }
            catch (StageException e) when (SshErrors.IsCommandFailure(e))
            {
                await connection.RunAsync($"rm -f {Quote(temporary)}", cancel);
                throw new StageException(Stage.Server, $"Ferry's agent can't run on {connection.Display}. Its home folder may not allow programs to run there, or its system may be too old. ({e.Message})", inner: e);
            }
            await connection.RunAsync($"mv -f {Quote(temporary)} {Quote(installed)}", cancel);
        }
        // older builds go; a running one keeps going until it is stopped (Linux keeps deleted files open)
        await connection.RunAsync(
            $"cd {Quote(bin)} && ln -sfn {Quote(name)} {Current} && find . -maxdepth 1 -name 'ferry-*' ! -name {Quote(name)} -delete", cancel);
        // the running agent's program, as Linux knows it: a removed older build reads "…/ferry-<hash> (deleted)"
        var pidFile = $"{bin[..bin.LastIndexOf('/')]}/run/agent.pid";
        var running = (await connection.RunAsync(
            $"pid=$(cat {Quote(pidFile)} 2>/dev/null); if [ -n \"$pid\" ]; then readlink /proc/$pid/exe 2>/dev/null; fi; true", cancel)).Trim();
        return running.Length > 0 && !running.EndsWith("/" + name, StringComparison.Ordinal);
    }

    public async Task StartAsync(CancellationToken cancel) =>
        await connection.RunAsync($"{Quote(await CurrentAsync(cancel))} agent start", cancel);

    /// <summary>Stop the running agent (any build: it is found by its pid file); its downloads carry on at the next start.</summary>
    public async Task StopAsync(CancellationToken cancel) =>
        await connection.RunAsync($"{Quote(await CurrentAsync(cancel))} agent stop", cancel);

    /// <summary>Stop the agent and remove it; downloaded files and the queue stay.</summary>
    public async Task RemoveAsync(CancellationToken cancel)
    {
        var bin = await BinFolderAsync(cancel);
        await connection.RunAsync($"if [ -x {Quote(bin)}/{Current} ]; then {Quote(bin)}/{Current} agent stop; fi; rm -rf {Quote(bin)}", cancel);
    }

    /// <summary>The command that joins the SSH channel to the agent, starting it if needed.</summary>
    public async Task<string> AttachCommandAsync(CancellationToken cancel) => $"{Quote(await CurrentAsync(cancel))} agent attach";

    /// <summary>Quote for a POSIX shell: inside single quotes, with any single quote closed, escaped and reopened.</summary>
    public static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // the home folder outlives an install, which makes the rest of the probe stale
    /// <summary>The agent's folder on the server.</summary>
    public async Task<string> RootAsync(CancellationToken cancel) => $"{_home ?? (await ProbeAsync(cancel)).Home}/{AgentPaths.FolderName}";

    private async Task<string> BinFolderAsync(CancellationToken cancel) => $"{_home ?? (await ProbeAsync(cancel)).Home}/{AgentPaths.FolderName}/{AgentPaths.BinFolderName}";

    /// <summary>The agent build's SHA-256, worked out once for as long as the file is the same (its length and time written).</summary>
    private static async Task<string> HashAsync(string path, CancellationToken cancel)
    {
        var info = new FileInfo(path);
        if (Hashes.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
            return known.Sha256;
        await using var file = File.OpenRead(path);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel));
        Hashes[path] = (info.Length, info.LastWriteTimeUtc, sha256);
        return sha256;
    }
}

/// <summary>What one probe of the server found (<see cref="AgentInstaller.ProbeAsync"/>).</summary>
/// <param name="Uname">"Linux x86_64"</param>
/// <param name="CurrentName">the build ~/.ferry/bin/current points at ("ferry-0123456789abcdef"); null when none is installed</param>
/// <param name="RunningExe">the running agent's program as Linux knows it; "… (deleted)" when it was replaced; null when none runs</param>
/// <param name="AttachAges">how long each "agent attach" on the server has run: one per connected Ferry (or one whose
/// connection died without the server noticing yet)</param>
public sealed record AgentFacts(string Home, string Uname, bool Musl, string? CurrentName, string? CurrentSha256, string? RunningExe,
                                IReadOnlyList<TimeSpan> AttachAges)
{
    /// <summary>Whether an agent of another build than <paramref name="name"/> runs, and must stop for this one to start.</summary>
    public bool RunsOtherThan(string name) => RunningExe is { Length: > 0 } running && !running.EndsWith("/" + name, StringComparison.Ordinal);

    /// <summary>The probe's "key=value" lines.</summary>
    internal static AgentFacts Parse(string answer)
    {
        var values = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Select(line => line.Split('=', 2))
                           .Where(pair => pair.Length == 2)
                           .GroupBy(pair => pair[0], StringComparer.Ordinal)
                           .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.Ordinal);
        string? Value(string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
        List<TimeSpan> attaches = [.. answer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                            .Where(line => line.StartsWith("attach=", StringComparison.Ordinal))
                                            .Select(line => long.TryParse(line["attach=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                                                ? TimeSpan.FromSeconds(seconds)
                                                : (TimeSpan?)null)
                                            .OfType<TimeSpan>()];
        return new AgentFacts(Value("home") ?? "", Value("uname") ?? "", Value("musl") == "yes", Value("current"), Value("sha256"), Value("running"),
                              attaches);
    }
}

/// <summary>Where the agent build to upload is, on this PC.</summary>
public static class AgentBinaries
{
    /// <summary>
    /// <see cref="Find"/>, else (when none was chosen) this version's build from its GitHub release, cached in
    /// <paramref name="cacheFolder"/>; null when there is neither.
    /// </summary>
    /// <exception cref="StageException">the release's file couldn't be fetched or checked (<see cref="Stage.Update"/>)</exception>
    public static async Task<string?> FindOrFetchAsync(string rid, string? chosen, string cacheFolder, CancellationToken cancel)
    {
        var found = Find(rid, chosen);
        if (found is not null || chosen is { Length: > 0 })
            return found; // a chosen build that isn't there is an error to report, not a reason to download
        using var http = new HttpFetcher();
        return await AgentDownloads.FetchAsync(new Releases(http), rid, cacheFolder, cancel);
    }

    /// <summary>
    /// The agent build for <paramref name="rid"/>: <paramref name="chosen"/> if given, else "ferry-{rid}" next to this
    /// program or in its "agent" folder, else this program itself when it is that build (the CLI on Linux).
    /// </summary>
    public static string? Find(string rid, string? chosen = null)
    {
        if (chosen is { Length: > 0 })
            return File.Exists(chosen) ? Path.GetFullPath(chosen) : null;
        var folder = AppContext.BaseDirectory;
        // beside the program, in its agent folder, or (the CLI the app installs, in cli\) in the app's agent folder
        foreach (var candidate in new[] { Path.Combine(folder, $"ferry-{rid}"), Path.Combine(folder, "agent", $"ferry-{rid}"),
                                          Path.Combine(folder, "..", "agent", $"ferry-{rid}") })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return RuntimeInformation.RuntimeIdentifier == rid && Environment.ProcessPath is { } self
               && Path.GetFileNameWithoutExtension(self) == "ferry"
            ? self
            : null;
    }
}
