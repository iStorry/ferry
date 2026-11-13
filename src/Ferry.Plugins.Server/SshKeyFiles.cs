using Ferry.Core.Files;
using Ferry.Core;
using Ferry.Core.Settings;

namespace Ferry.Plugins.Server;

/// <summary>Where server mode keeps its OpenSSH files on this PC: a private "ssh" folder beside the settings.</summary>
public sealed record OpenSshPaths(string Folder)
{
    public static OpenSshPaths For(AppPaths paths) => new(Path.Combine(paths.ConfigFolder, "ssh"));

    /// <summary>Beside <paramref name="secrets"/>' folder, so whatever moves the secrets (FERRY_CONFIG_PATH) moves these too.</summary>
    public static OpenSshPaths For(SecretStore secrets) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(secrets.Folder))!, "ssh"));

    /// <summary>Ferry's own known_hosts: the one key the user confirmed.</summary>
    public string KnownHosts => Path.Combine(Folder, "known_hosts");

    /// <summary>The key Ferry makes at setup when the user logs in with a password.</summary>
    public string OwnKey => Path.Combine(Folder, "ferry_ed25519");

    /// <summary>A key that was in the secret store before OpenSSH (Ferry's P-256, or the user's), now a file.</summary>
    public string MigratedKey => Path.Combine(Folder, "ferry_key");
}

/// <summary>Private key files, through ssh-keygen: making Ferry's own, and checking one the user picked.</summary>
public static class SshKeyFiles
{
    /// <summary>How a PuTTY key file starts; OpenSSH can't read those.</summary>
    internal const string PuttyHeader = "PuTTY-User-Key-File";

    /// <summary>
    /// Make a new Ed25519 key at <paramref name="path"/> (replacing one there), with no passphrase, private to this
    /// user. Returns its public half as an authorized_keys line.
    /// </summary>
    /// <exception cref="StageException">ssh-keygen failed (<see cref="Stage.Server"/>)</exception>
    public static async Task<string> CreateAsync(SshTools tools, string path, string comment, CancellationToken cancel)
    {
        PrivateFiles.CreateFolder(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // ssh-keygen would ask whether to overwrite, and there's no one to answer
        File.Delete(path);
        File.Delete(path + ".pub");
        var result = await SshProcess.RunAsync(tools.Keygen, ["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", path], null, cancel);
        if (result.ExitCode != 0 || !File.Exists(path) || !File.Exists(path + ".pub"))
            throw new StageException(Stage.Server, $"ssh-keygen couldn't make a key: {Last(result.Errors)} (exit {result.ExitCode}).");
        PrivateFiles.Restrict(path);
        return (await File.ReadAllTextAsync(path + ".pub", cancel)).Trim();
    }

    /// <summary>Whether the key at <paramref name="path"/> is encrypted and needs a passphrase.</summary>
    /// <exception cref="StageException">it isn't a private key OpenSSH can use, or other users can read it</exception>
    public static async Task<bool> NeedsPassphraseAsync(SshTools tools, string path, CancellationToken cancel)
    {
        if (!File.Exists(path))
            throw new StageException(Stage.Server, $"There is no key file at {path}.");
        var result = await SshProcess.RunAsync(tools.Keygen, ["-y", "-P", "", "-f", path], null, cancel);
        if (result.ExitCode == 0)
            return false;
        if (result.Errors.Contains("incorrect passphrase", StringComparison.OrdinalIgnoreCase))
            return true;
        throw Unusable(path, result.Errors);
    }

    /// <summary>Why ssh-keygen (or ssh) can't use the key at <paramref name="path"/>, for the user.</summary>
    internal static StageException Unusable(string path, string errors)
    {
        if (errors.Contains("UNPROTECTED PRIVATE KEY FILE", StringComparison.OrdinalIgnoreCase)
            || errors.Contains("bad permissions", StringComparison.OrdinalIgnoreCase))
        {
            return new StageException(Stage.Server,
                $"Other users can read {path}, so OpenSSH won't use it. Make it readable by you alone " +
                (OperatingSystem.IsWindows() ? "(Properties → Security → Advanced: remove inheritance and every other user)." : "(chmod 600)."));
        }
        if (IsPutty(path))
        {
            return new StageException(Stage.Server,
                $"{path} is a PuTTY key, which OpenSSH can't read. In PuTTYgen, load it and use Conversions → Export OpenSSH key.");
        }
        return new StageException(Stage.Server, $"{path} is not a private key OpenSSH can use: {Last(errors)}");
    }

    private static bool IsPutty(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return reader.ReadLine()?.StartsWith(PuttyHeader, StringComparison.Ordinal) == true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Last(string errors) =>
        errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no reason given";
}
