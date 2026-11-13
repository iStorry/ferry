using Ferry.Core.Settings;
using Ferry.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ferry.Plugins.Server;

/// <summary>
/// The OpenSSH client programs server mode runs: <c>ssh</c> and <c>ssh-keygen</c>, from one folder, with the client's
/// version. Found in this order: <see cref="Settings.ServerKeys.SshPath"/>, the "openssh" folder the app carries, Windows'
/// own client (System32\OpenSSH), then PATH outside Windows.
/// </summary>
/// <param name="VersionText">what <c>ssh -V</c> said ("OpenSSH_for_Windows_9.5p2, LibreSSL 3.8.2")</param>
public sealed partial record SshTools(string Ssh, string Keygen, Version Version, string VersionText)
{
    /// <summary>The oldest client that works: passwords and passphrases go through SSH_ASKPASS_REQUIRE, new in 8.4.</summary>
    public static readonly Version Minimum = new(8, 4);

    private static readonly TimeSpan VersionWait = TimeSpan.FromSeconds(10);

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>"OpenSSH 9.5 · C:\Windows\System32\OpenSSH\ssh.exe", for the user.</summary>
    public string Display => $"OpenSSH {Version} · {Ssh}";

    /// <summary>The client <see cref="FindAsync"/> would use, or why there is none (with how to fix it): for a front end
    /// to show before anything needs it.</summary>
    public static async Task<(SshTools? Tools, string? Problem)> CheckAsync(string? configured, CancellationToken cancel)
    {
        try
        {
            return (await FindAsync(configured, cancel), null);
        }
        catch (StageException e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>Find the client and check its version.</summary>
    /// <param name="configured">the setting: a folder, or the ssh program itself; empty for "find it"</param>
    /// <exception cref="StageException">no client, one without ssh-keygen beside it, or one older than
    /// <see cref="Minimum"/> (<see cref="Stage.Server"/>), each saying how to fix it</exception>
    public static async Task<SshTools> FindAsync(string? configured, CancellationToken cancel)
    {
        var ssh = Candidates(configured).FirstOrDefault(File.Exists)
                  ?? throw new StageException(Stage.Server, configured is { Length: > 0 }
                      ? $"There is no OpenSSH client at {configured} (FERRY_SSH_PATH)."
                      : NotFound());
        var keygen = Path.Combine(Path.GetDirectoryName(ssh)!, Exe("ssh-keygen"));
        if (!File.Exists(keygen))
            throw new StageException(Stage.Server, $"{ssh} has no ssh-keygen beside it; Ferry needs both.");
        var text = await VersionOfAsync(ssh, cancel);
        var version = ParseVersion(text)
                      ?? throw new StageException(Stage.Server, $"{ssh} doesn't look like OpenSSH (it says \"{text}\").");
        if (version < Minimum)
        {
            throw new StageException(Stage.Server,
                $"{ssh} is OpenSSH {version}; Ferry needs {Minimum} or newer. " +
                (OperatingSystem.IsWindows() ? "Update Windows, or set FERRY_SSH_PATH to a newer Win32-OpenSSH." : "Update OpenSSH."));
        }
        return new SshTools(ssh, keygen, version, text);
    }

    /// <summary>Where ssh may be, in the order it is looked for.</summary>
    internal static IEnumerable<string> Candidates(string? configured)
    {
        if (configured is { Length: > 0 })
        {
            // a folder, or the program; nothing else is tried, so a wrong setting is reported rather than skipped
            var path = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            yield return Directory.Exists(path) ? Path.Combine(path, Exe("ssh")) : path;
            yield break;
        }
        yield return Path.Combine(AppContext.BaseDirectory, "openssh", Exe("ssh"));
        // the CLI the app installs is in cli\, beside the app's openssh\
        yield return Path.Combine(AppContext.BaseDirectory, "..", "openssh", Exe("ssh"));
        if (OperatingSystem.IsWindows())
        {
            // Git's ssh on PATH is an MSYS program that reads Windows paths its own way: only Windows' own is used
            yield return Path.Combine(Environment.SystemDirectory, "OpenSSH", Exe("ssh"));
            yield break;
        }
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(folder, "ssh");
    }

    /// <summary>"OpenSSH_for_Windows_9.5p2, LibreSSL 3.8.2" → 9.5; "OpenSSH_10.0p2 Debian-7" → 10.0; null when it isn't OpenSSH.</summary>
    internal static Version? ParseVersion(string text) =>
        VersionPattern().Match(text) is { Success: true } match
            ? new Version(int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
                          int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture))
            : null;

    private static async Task<string> VersionOfAsync(string ssh, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(ssh, ["-V"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        wait.CancelAfter(VersionWait);
        Process? process = null;
        try
        {
            process = Process.Start(info) ?? throw new StageException(Stage.Server, $"{ssh} could not be started.");
            process.StandardInput.Close();
            // it prints the version on stderr
            var (output, errors) = (process.StandardOutput.ReadToEndAsync(wait.Token), process.StandardError.ReadToEndAsync(wait.Token));
            await process.WaitForExitAsync(wait.Token);
            return ((await errors).Trim() + " " + (await output).Trim()).Trim();
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            Kill(process);
            throw new StageException(Stage.Server, $"{ssh} -V didn't answer within {VersionWait.TotalSeconds:0} s.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new StageException(Stage.Server, $"{ssh} could not be started: {e.Message}", inner: e);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void Kill(Process? process)
    {
        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // it ended meanwhile
        }
    }

    private static string NotFound() =>
        OperatingSystem.IsWindows()
            ? "Windows' OpenSSH client isn't installed. Turn it on in Settings → System → Optional features → OpenSSH Client, " +
              "or set FERRY_SSH_PATH to an OpenSSH client (8.4 or newer)."
            : "There is no ssh on PATH. Install the OpenSSH client (8.4 or newer), or set FERRY_SSH_PATH.";

    [GeneratedRegex(@"OpenSSH_(?:for_Windows_)?(?<major>\d+)\.(?<minor>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
