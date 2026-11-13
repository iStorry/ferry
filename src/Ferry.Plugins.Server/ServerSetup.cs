using Ferry.Core.Files;
using Ferry.Core;
using System.Globalization;
using Ferry.Core.Settings;

namespace Ferry.Plugins.Server;

/// <param name="Password">log in with this once, to add a key of Ferry's own; it is not saved. Null when
/// <paramref name="KeyFile"/> is given.</param>
/// <param name="KeyFile">log in with this private key file, used where it is (not copied)</param>
/// <param name="Passphrase">the key's passphrase, when it has one; saved in the secret store</param>
/// <param name="AgentBinary">the agent build to upload; found next to this program when null</param>
/// <param name="ExpectedHostKey">the server's fingerprint, when the user already knows it (no question asked)</param>
public sealed record ServerSetupRequest(string Host, int Port, string User, string? Password = null, string? KeyFile = null,
                                        string? Passphrase = null, string? AgentBinary = null, string? ExpectedHostKey = null);

/// <param name="FreeBytes">free space in the server's download folder, when the agent can tell</param>
/// <param name="CreatedKey">Ferry made a key of its own (the user logged in with a password)</param>
/// <param name="KeyFile">the private key file Ferry logs in with from now on</param>
public sealed record ServerSetupResult(string Display, string HostKey, string Rid, string AgentVersion, long? FreeBytes, bool CreatedKey,
                                       string KeyFile = "");

/// <summary>
/// Setting up server mode (§4.5, docs/openssh-plan.md §4): record the server's key and have it confirmed, swap a
/// password for a key of Ferry's own, check the server, upload and start the agent, and say hello. Only once all of
/// that worked are the key and known_hosts moved into place and the settings (mode, host, user, fingerprint, key file)
/// saved; until then they are scratch files beside them.
/// </summary>
public static class ServerSetup
{
    private static readonly TimeSpan HelloLimit = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TakeBackLimit = TimeSpan.FromSeconds(20);

    /// <param name="trustNewHostKey">shown the server's fingerprint; true to trust it. Not asked when the request
    /// names the expected key.</param>
    /// <param name="progress">each step as it starts, for the user</param>
    /// <exception cref="StageException">a step failed (<see cref="Stage.Server"/>); nothing was saved on this PC, and a key added on the
    /// server for this setup is taken off again if it can be. An agent uploaded before the failure stays there, for next time.</exception>
    /// <exception cref="HostKeyChangedException">the server's key is not the expected one</exception>
    public static async Task<ServerSetupResult> RunAsync(ServerSetupRequest request, SettingsStore store, SecretStore secrets,
                                                         Func<string, bool> trustNewHostKey, Action<string> progress, CancellationToken cancel)
    {
        if (request.Password is null && request.KeyFile is null)
            throw new ArgumentException("Setup needs a password or a key file.", nameof(request));
        var settings = store.Load();
        var keepalive = settings.Duration(ServerKeys.ServerKeepalive);
        var profile = new ServerProfile(request.Host, request.Port, request.User, null);
        var paths = OpenSshPaths.For(secrets);
        var tools = await SshTools.FindAsync(settings.Text(ServerKeys.SshPath), cancel);

        progress($"Connecting to {profile.Display}");
        var offered = await KnownHosts.RecordAsync(tools, profile, cancel);
        if (request.ExpectedHostKey is { } expected)
        {
            if (!ServerProfile.SameKey(expected, offered.Fingerprint))
                throw new HostKeyChangedException(profile.Display, expected, offered.Fingerprint);
        }
        else if (!trustNewHostKey(offered.Fingerprint))
        {
            throw new StageException(Stage.Server, $"The host key of {profile.Display} ({offered.Fingerprint}) was not accepted.");
        }
        var confirmed = profile with { HostKey = offered.Fingerprint };

        PrivateFiles.CreateFolder(paths.Folder);
        var scratchHosts = paths.KnownHosts + ".setup";
        var newKey = paths.OwnKey + ".setup";
        string? authorized = null;
        try
        {
            KnownHosts.Save(scratchHosts, offered);
            OpenSshLogin login;
            var savePassphrase = false;
            if (request.Password is { } password)
            {
                progress("Adding a key for Ferry to ~/.ssh/authorized_keys");
                var authorizedKey = await SshKeyFiles.CreateAsync(tools, newKey, $"ferry@{Environment.MachineName}", cancel);
                await ServerConnection.WithPassword(tools, confirmed, password, scratchHosts, keepalive).RunAsync(AuthorizeCommand(authorizedKey), cancel);
                authorized = authorizedKey;
                login = new OpenSshLogin(newKey, null);
            }
            else
            {
                var keyFile = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.KeyFile!));
                savePassphrase = await SshKeyFiles.NeedsPassphraseAsync(tools, keyFile, cancel);
                if (savePassphrase && string.IsNullOrEmpty(request.Passphrase))
                    throw new StageException(Stage.Server, "The key has a passphrase; give it too.");
                login = new OpenSshLogin(keyFile, savePassphrase ? request.Passphrase : null);
            }
            var connection = ServerConnection.WithKey(tools, confirmed, login, scratchHosts, keepalive);
            var installer = new AgentInstaller(connection);
            progress("Checking the server");
            string rid;
            try
            {
                rid = await installer.DetectAsync(cancel);
            }
            catch (StageException e) when (authorized is not null && SshErrors.IsLoginRefused(e))
            {
                throw new StageException(Stage.Server, $"{profile.Display} took the password and Ferry added its key, but then refused that key. The server may not " +
                                                       "accept keys for this account, or your home folder or ~/.ssh may be writable by others; choosing A key file won't help until that's sorted out.", inner: e);
            }
            var binary = await AgentBinaries.FindOrFetchAsync(rid, request.AgentBinary, AgentCache(settings), cancel)
                         ?? throw new StageException(Stage.Server, NoAgentBuild(rid, request.AgentBinary));
            await installer.EnsureNotInUseByAnotherBuildAsync(binary, cancel);
            progress($"Uploading the agent ({rid})");
            if (await installer.InstallAsync(binary, cancel))
                await installer.StopAsync(cancel); // an agent of another build, from an earlier setup
            progress("Starting the agent");
            await installer.StartAsync(cancel);

            var keyPath = request.Password is null ? login.KeyFile : paths.OwnKey;
            var saved = new Dictionary<string, string>
            {
                [ServerKeys.Enabled.Name] = "true",
                [ServerKeys.Mode.Name] = "server",
                [ServerKeys.ServerHost.Name] = request.Host,
                [ServerKeys.ServerPort.Name] = request.Port.ToString(CultureInfo.InvariantCulture),
                [ServerKeys.ServerUser.Name] = request.User,
                [ServerKeys.ServerHostKey.Name] = offered.Fingerprint,
                [ServerKeys.ServerKey.Name] = keyPath,
            };
            if (request.AgentBinary is not null)
                saved[ServerKeys.ServerAgent.Name] = binary;
            var planned = settings.Changed();
            foreach (var (key, value) in saved)
                planned[key] = value;
            progress("Talking to the agent");
            var (version, free, session) = await HelloAsync(connection, installer, planned, cancel);
            await session.DisposeAsync();

            // all of it worked: the scratch files take their places, and a key moved over before OpenSSH isn't needed
            KnownHosts.Save(paths.KnownHosts, offered);
            if (request.Password is not null)
            {
                File.Move(newKey, paths.OwnKey, overwrite: true);
                File.Move(newKey + ".pub", paths.OwnKey + ".pub", overwrite: true);
            }
            File.Delete(paths.MigratedKey);
            if (savePassphrase)
                new ServerLogin(Passphrase: request.Passphrase).Save(secrets);
            else
                ServerLogin.Forget(secrets);
            foreach (var (key, value) in saved)
                store.Set(key, value);
            return new ServerSetupResult(profile.Display, offered.Fingerprint, rid, version, free, request.Password is not null, keyPath);
        }
        catch (Exception) when (authorized is not null)
        {
            await TakeKeyBackAsync(tools, confirmed, request.Password!, scratchHosts, keepalive, authorized);
            throw;
        }
        finally
        {
            File.Delete(scratchHosts);
            File.Delete(newKey);
            File.Delete(newKey + ".pub");
        }
    }

    /// <summary>Whether the key file at <paramref name="keyFile"/> has a passphrase, so the front end asks for it.</summary>
    /// <exception cref="StageException">no OpenSSH client, or it isn't a key OpenSSH can use (<see cref="Stage.Server"/>)</exception>
    public static async Task<bool> KeyNeedsPassphraseAsync(FerrySettings settings, string keyFile, CancellationToken cancel)
    {
        var tools = await SshTools.FindAsync(settings.Text(ServerKeys.SshPath), cancel);
        return await SshKeyFiles.NeedsPassphraseAsync(tools, Path.GetFullPath(Environment.ExpandEnvironmentVariables(keyFile)), cancel);
    }

    /// <summary>Stop the agent on the server set up in <paramref name="settings"/> and remove it; its files and queue stay.</summary>
    /// <exception cref="StageException">the server can't be reached, or there's no login</exception>
    /// <exception cref="HostKeyChangedException">the server's key is not the confirmed one</exception>
    public static async Task RemoveAgentAsync(FerrySettings settings, SecretStore secrets, CancellationToken cancel)
    {
        await using var connection = await ServerConnection.OpenAsync(settings, secrets, cancel);
        await new AgentInstaller(connection).RemoveAsync(cancel);
    }

    /// <summary>Remove the server from this PC: its login and settings, and back to local mode.</summary>
    public static void Forget(SettingsStore store, SecretStore secrets)
    {
        ServerLogin.Forget(secrets);
        // Ferry's own files; a key the user chose stays where it is
        var paths = OpenSshPaths.For(secrets);
        foreach (var file in new[] { paths.OwnKey, paths.OwnKey + ".pub", paths.MigratedKey, paths.KnownHosts })
        {
            if (File.Exists(file)) // a setup from before OpenSSH has no ssh folder, and deleting in one that's missing throws
                File.Delete(file);
        }
        foreach (var key in new[] { ServerKeys.ServerHost, ServerKeys.ServerPort, ServerKeys.ServerUser, ServerKeys.ServerHostKey, ServerKeys.ServerKey,
                                    ServerKeys.ServerAgent })
            store.Unset(key.Name);
        store.Set(ServerKeys.Mode.Name, "local");
    }

    /// <summary>Where agent builds fetched from GitHub are kept.</summary>
    public static string AgentCache(FerrySettings settings) => Path.Combine(settings.Paths.ConfigFolder, "agents");

    public static string NoAgentBuild(string rid, string? chosen) =>
        chosen is not null
            ? $"The agent build {chosen} doesn't exist."
            : $"There is no agent build for {rid} here, and no GitHub release of Ferry {Product.Version} to fetch it from. " +
              $"Put ferry-{rid} (the Linux build of ferry) next to this program, or choose it with FERRY_SERVER_AGENT " +
              "(or --agent at setup). In a build of your own, tools/publish-agent.ps1 makes it.";

    /// <summary>The server's agent is another version and nothing here could replace it: what to do, for the user, with no retry.</summary>
    public static string AgentNotReplaced(AgentVersionException mismatch) =>
        $"{mismatch.Message} It wasn't replaced: put this version's agent build (ferry-linux-x64 or ferry-linux-arm64, the Linux build of ferry) " +
        "next to this program, or choose it with FERRY_SERVER_AGENT, then connect again. In a build of your own, tools/publish-agent.ps1 makes it.";

    /// <summary>Append <paramref name="authorizedKey"/> to ~/.ssh/authorized_keys unless it is there, with private permissions.</summary>
    internal static string AuthorizeCommand(string authorizedKey)
    {
        var line = AgentInstaller.Quote(authorizedKey);
        // sshd ignores a file others can write to, and a last line with no end would join on to this one
        return "umask 077 && mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys && " +
               "if [ -n \"$(tail -c 1 ~/.ssh/authorized_keys)\" ]; then echo >> ~/.ssh/authorized_keys; fi && " +
               $"(grep -qxF {line} ~/.ssh/authorized_keys || printf '%s\\n' {line} >> ~/.ssh/authorized_keys)";
    }

    /// <summary>Take <paramref name="authorizedKey"/> out of ~/.ssh/authorized_keys again.</summary>
    internal static string UnauthorizeCommand(string authorizedKey) =>
        "umask 077 && f=~/.ssh/authorized_keys && " +
        $"{{ grep -vxF {AgentInstaller.Quote(authorizedKey)} \"$f\" || true; }} > \"$f.ferry\" && mv -f \"$f.ferry\" \"$f\"";

    /// <summary>The setup stopped: its key, whose private half is about to go, shouldn't stay on the server. Only tidying, so a failure here says nothing.</summary>
    private static async Task TakeKeyBackAsync(SshTools tools, ServerProfile profile, string password, string knownHosts, TimeSpan keepalive, string authorizedKey)
    {
        using var limit = new CancellationTokenSource(TakeBackLimit);
        try
        {
            await ServerConnection.WithPassword(tools, profile, password, knownHosts, keepalive).RunAsync(UnauthorizeCommand(authorizedKey), limit.Token);
        }
        catch (Exception e) when (e is StageException or HostKeyChangedException or OperationCanceledException or IOException)
        {
            // an unused line in a file, and the reason the setup stopped is what matters
        }
    }

    /// <summary>
    /// Hello to the agent just started, with the settings it will run with; an older running build is replaced. The
    /// talk stays open for more questions, and ends when the returned session is disposed.
    /// </summary>
    private static async Task<(string Version, long? Free, AgentTalk Session)> HelloAsync(
        ServerConnection connection, AgentInstaller installer, Dictionary<string, string> settings, CancellationToken cancel)
    {
        for (var attempt = 1; ; attempt++)
        {
            var channel = connection.OpenChannel(await installer.AttachCommandAsync(cancel));
            var talk = new AgentTalk(channel, new AgentClient(channel.FromAgent, channel.ToAgent, _ => { }, () => { }));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(HelloLimit);
            try
            {
                AgentMessage hello;
                try
                {
                    hello = await talk.Client.SendAsync(new AgentRequest(0, AgentCommand.Hello, Version: Product.Build, Settings: settings), limit.Token);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new StageException(Stage.Server, $"The agent on {connection.Display} didn't answer within a minute. {channel.Errors.Trim()}".TrimEnd(), isRetryable: true);
                }
                if (hello.Version is { } version && version != Product.Build && attempt == 1)
                {
                    await talk.DisposeAsync();
                    await installer.StopAsync(cancel); // an older agent from an earlier setup; attach starts the new one
                    continue;
                }
                if (hello.Error is { } error)
                    throw new StageException(Stage.Server, $"The agent on {connection.Display} refused: {error}");
                var space = await talk.Client.SendAsync(new AgentRequest(0, AgentCommand.Space), cancel);
                return (hello.Version ?? "?", space.FreeBytes, talk);
            }
            catch
            {
                await talk.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>A talk with the agent over one SSH channel; disposing ends both.</summary>
    private sealed class AgentTalk(AgentChannel channel, AgentClient client) : IAsyncDisposable
    {
        public AgentClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await channel.DisposeAsync();
        }
    }
}
