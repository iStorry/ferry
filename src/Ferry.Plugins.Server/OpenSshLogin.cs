using Ferry.Core.Files;
using Ferry.Core;
using Ferry.Core.Settings;

namespace Ferry.Plugins.Server;

/// <summary>
/// How ssh logs in to the server: a private key file (<see cref="ServerKeys.ServerKey"/>), and its passphrase when it
/// has one (kept in the <see cref="SecretStore"/>, handed over through <see cref="Askpass"/>). After setup it is keys
/// only: a password is used once, to add Ferry's own key (docs/openssh-plan.md, decision 2).
/// </summary>
public sealed record OpenSshLogin(string KeyFile, string? Passphrase)
{
    /// <summary>
    /// The login to use: the key file the setup chose (<see cref="ServerKeys.ServerKey"/>). An install from before
    /// OpenSSH has its key in the secret store instead: it is written to a private file
    /// (<see cref="OpenSshPaths.MigratedKey"/>), found there by its name from then on. The key stays in the secret store
    /// too until <see cref="CompleteMigration"/>, once a connection with the file has worked.
    /// </summary>
    /// <exception cref="StageException">there is no key to log in with (none saved, a saved password, a PuTTY key):
    /// the server setup has to run again (<see cref="Stage.Server"/>, not retryable)</exception>
    public static OpenSshLogin Prepare(FerrySettings settings, SecretStore secrets, OpenSshPaths paths)
    {
        var display = ServerProfile.From(settings).Display;
        var saved = ServerLogin.Load(secrets);
        if (settings.Text(ServerKeys.ServerKey) is { Length: > 0 } keyFile)
        {
            return File.Exists(keyFile)
                ? new OpenSshLogin(keyFile, saved?.Passphrase)
                : throw new StageException(Stage.Server,
                    $"The key Ferry logs in to {display} with is gone ({keyFile}); run the server setup again.");
        }
        if (saved?.PrivateKey is { } key)
        {
            if (key.TrimStart().StartsWith(SshKeyFiles.PuttyHeader, StringComparison.Ordinal))
            {
                throw new StageException(Stage.Server,
                    $"The key for {display} is a PuTTY key, which OpenSSH can't read; run the server setup again with an " +
                    "OpenSSH key (PuTTYgen: Conversions → Export OpenSSH key), or with the password.");
            }
            PrivateFiles.WriteText(paths.MigratedKey, Normalise(key));
            return new OpenSshLogin(paths.MigratedKey, saved.Passphrase);
        }
        if (File.Exists(paths.MigratedKey))
            return new OpenSshLogin(paths.MigratedKey, saved?.Passphrase); // moved over, and the secret store let go of it
        if (saved?.Password is not null)
        {
            throw new StageException(Stage.Server,
                $"Ferry no longer keeps a password for {display}; run the server setup again. It uses the password once, " +
                "to add a key of Ferry's own.");
        }
        throw new StageException(Stage.Server, $"There is no login saved for {display}; run the server setup again.");
    }

    /// <summary>A connection with the migrated key file worked: the key leaves the secret store, the passphrase stays.</summary>
    public static void CompleteMigration(SecretStore secrets)
    {
        if (ServerLogin.Load(secrets) is not { PrivateKey: not null } saved)
            return;
        if (saved.Passphrase is { } passphrase)
            new ServerLogin(Passphrase: passphrase).Save(secrets);
        else
            ServerLogin.Forget(secrets);
    }

    /// <summary>OpenSSH wants the key's lines ended by "\n", the last one too.</summary>
    internal static string Normalise(string key) => key.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";
}
