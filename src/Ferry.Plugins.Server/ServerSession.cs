using Ferry.Core.Files;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Plugins;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.Official;

namespace Ferry.Plugins.Server;

/// <summary>
/// Server mode's connection as a whole: log in, make sure this build's agent is the one running (replacing an older
/// one), attach <see cref="Queue"/> to it over SSH, and reconnect with backoff when the connection drops. The server
/// keeps downloading meanwhile.
/// </summary>
public sealed class ServerSession : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly FerrySettings _settings;
    private readonly ServerProfile _profile;
    private readonly SecretStore _secrets;
    private readonly PluginStore? _plugins;
    private readonly ConnectionFiles _files = new();
    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _gaveUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();
    private ServerConnection? _connection;
    private AgentChannel? _channel;
    private Task _reconnecting = Task.CompletedTask;
    private bool _reconnectLoop;    // under _lock: ReconnectAsync is running
    private bool _droppedAgain;     // under _lock: the connection dropped while it ran, so it must not stop yet

    private ServerSession(FerrySettings settings, SecretStore secrets, IReadOnlyDictionary<Guid, RequestDetails>? requests, Action<string, Exception>? log,
                          PluginStore? plugins)
    {
        (_settings, _profile, _secrets, _plugins) = (settings, ServerProfile.From(settings), secrets, plugins);
        Queue = new RemoteQueue(settings, _files, requests, log);
        Queue.ConnectionChanged += OnConnectionChanged;
    }

    public RemoteQueue Queue { get; }

    public string Display => _profile.Display;

    /// <summary>
    /// A reconnection try failed; after a <see cref="HostKeyChangedException"/> or an <see cref="AgentVersionException"/>
    /// there are no more tries, as only the user can put that right.
    /// </summary>
    public event Action<Exception>? ReconnectFailed;

    /// <summary>Completes after a <see cref="ReconnectFailed"/> with no more tries: nothing will come from the server now without the user.</summary>
    public Task GaveUp => _gaveUp.Task;

    /// <summary>Connect to the server set up in <paramref name="settings"/>, with the login saved beside <paramref name="secrets"/>.</summary>
    /// <param name="requests">the request details saved from last time (<see cref="RequestStore"/>), by item, for the queue</param>
    /// <param name="log">where the queue notes a listener's failure, or a line from the agent that isn't a message</param>
    /// <param name="holdTransfers">nothing comes over until the queue is let go (<see cref="RemoteQueue.SetHeldAsync"/>): for
    /// what only looks at the server, and for what has to be listening first</param>
    /// <param name="plugins">the plugins installed on this PC, which the server is to have, no more and no fewer; null to leave the server's as they are</param>
    /// <exception cref="HostKeyChangedException">the server's key is not the confirmed one</exception>
    /// <exception cref="StageException">no server set up, no login, no OpenSSH client, the server can't be reached, or
    /// its agent can't be started (<see cref="Stage.Server"/>)</exception>
    /// <exception cref="AgentVersionException">the server runs another build and there's no agent build here to replace it</exception>
    public static async Task<ServerSession> OpenAsync(FerrySettings settings, SecretStore secrets, CancellationToken cancel,
                                                      IReadOnlyDictionary<Guid, RequestDetails>? requests = null, Action<string, Exception>? log = null,
                                                      bool holdTransfers = false, PluginStore? plugins = null)
    {
        var session = new ServerSession(settings, secrets, requests, log, plugins);
        try
        {
            await session.Queue.SetHeldAsync(holdTransfers);
            await session.ConnectAsync(cancel);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        return session;
    }

    /// <summary>What turns links to many files into the files: the server reads them, but for what only this PC can.</summary>
    public LinkExpander Expander(HttpFetcher http) =>
        new([.. OfficialSites.Listers(http, _settings, _secrets, downloadsElsewhere: true), new RemoteLister(Queue)]);

    /// <summary>Stop the transfers (what arrived is kept) and disconnect; the server carries on.</summary>
    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        await Queue.DisposeAsync();
        try
        {
            await _reconnecting;
        }
        catch (OperationCanceledException)
        {
            // stopped on purpose
        }
        await DropAsync(null, null);
        _connecting.Dispose();
        _closing.Dispose();
    }

    /// <summary>
    /// The secrets the installed plugins declare, as they are saved on this PC now, for the agent: with the next settings it
    /// is sent, or the next hello. Only theirs: what comes with Ferry keeps its secrets here.
    /// </summary>
    public void RefreshPluginSecrets()
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var secret in (_plugins?.List(out _) ?? []).SelectMany(plugin => plugin.Manifest.Secrets))
        {
            if (_secrets.Load(secret.Name) is { Length: > 0 } bytes)
                secrets[secret.Name] = System.Text.Encoding.UTF8.GetString(bytes);
        }
        Queue.PluginSecrets = secrets;
    }

    private async Task ConnectAsync(CancellationToken cancel)
    {
        await _connecting.WaitAsync(cancel);
        try
        {
            RefreshPluginSecrets();
            // a connection is only how to run ssh: the probe below is the first login, the attach the second
            var connection = await ServerConnection.OpenAsync(_settings, _secrets, cancel);
            try
            {
                var installer = new AgentInstaller(connection);
                var rid = await installer.DetectAsync(cancel);
                var chosen = _settings.Text(ServerKeys.ServerAgent);
                string? binary;
                try
                {
                    binary = await AgentBinaries.FindOrFetchAsync(rid, chosen.Length > 0 ? chosen : null, ServerSetup.AgentCache(_settings), cancel);
                }
                catch (StageException e) when (e.Stage == Stage.Update)
                {
                    binary = null; // GitHub out of reach: the agent installed before still serves
                }
                if (binary is not null)
                {
                    await installer.EnsureNotInUseByAnotherBuildAsync(binary, cancel);
                    if (await installer.InstallAsync(binary, cancel))
                        await installer.StopAsync(cancel); // an agent of another build stops; attach starts this one
                }
                else if (!await installer.IsInstalledAsync(cancel))
                    throw new StageException(Stage.Server, ServerSetup.NoAgentBuild(rid, chosen.Length > 0 ? chosen : null));
                // an agent loads its plugins as it starts: with other plugins it stops, and attach starts it again
                if (_plugins is not null && await PluginSync.SyncAsync(connection, await installer.RootAsync(cancel), _plugins.List(out _), cancel))
                    await installer.StopAsync(cancel);
                AgentChannel channel;
                try
                {
                    channel = await AttachAsync(connection, installer, cancel);
                }
                catch (AgentVersionException) when (binary is not null)
                {
                    await installer.StopAsync(cancel); // the older build stops; attach starts this one
                    channel = await AttachAsync(connection, installer, cancel);
                }
                await DropAsync(connection, channel);
                OpenSshLogin.CompleteMigration(_secrets); // the key file works: the secret store lets go of the key
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _connecting.Release();
        }
    }

    private async Task<AgentChannel> AttachAsync(ServerConnection connection, AgentInstaller installer, CancellationToken cancel)
    {
        var channel = connection.OpenChannel(await installer.AttachCommandAsync(cancel));
        try
        {
            _files.Current = connection; // transfers can start as soon as the queue has its connection
            await Queue.AttachAsync(channel.FromAgent, channel.ToAgent, cancel);
            return channel;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    /// <summary>Switch to a new connection (or none), closing the old one.</summary>
    private async Task DropAsync(ServerConnection? connection, AgentChannel? channel)
    {
        ServerConnection? oldConnection;
        AgentChannel? oldChannel;
        lock (_lock)
        {
            (oldConnection, _connection) = (_connection, connection);
            (oldChannel, _channel) = (_channel, channel);
            _files.Current = connection;
        }
        if (oldChannel is not null)
            await oldChannel.DisposeAsync();
        if (oldConnection is not null)
            await oldConnection.DisposeAsync();
    }

    private void OnConnectionChanged(bool attached)
    {
        if (attached || _closing.IsCancellationRequested)
            return;
        lock (_lock)
        {
            if (_reconnectLoop)
            {
                _droppedAgain = true;
                return;
            }
            _reconnectLoop = true;
            _reconnecting = ReconnectAsync(_closing.Token);
        }
    }

    /// <summary>Whether the reconnection is done: attached, and no drop came in while it ran.</summary>
    private bool Reconnected()
    {
        lock (_lock)
        {
            if (_droppedAgain || !Queue.IsAttached)
            {
                _droppedAgain = false;
                return false;
            }
            _reconnectLoop = false;
            return true;
        }
    }

    private void ReconnectStopped()
    {
        lock (_lock)
            (_reconnectLoop, _droppedAgain) = (false, false);
    }

    private async Task ReconnectAsync(CancellationToken closing)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await Task.Delay(Backoff[Math.Min(attempt, Backoff.Length - 1)], closing);
                if (Reconnected())
                    return; // attached again meanwhile
                await ConnectAsync(closing);
                if (Reconnected())
                    return;
                attempt = -1; // attached, then dropped again at once: start the backoff afresh
            }
            catch (OperationCanceledException) when (closing.IsCancellationRequested)
            {
                ReconnectStopped();
                return;
            }
            catch (Exception e) when (e is HostKeyChangedException or AgentVersionException)
            {
                ReconnectStopped();
                ReconnectFailed?.Invoke(e);
                _gaveUp.TrySetResult();
                return; // never retried without the user
            }
#pragma warning disable CA1031 // whatever went wrong (a full disk for the agent cache, say), the loop must go on trying
            catch (Exception e)
#pragma warning restore CA1031
            {
                ReconnectFailed?.Invoke(e);
            }
        }
    }

    /// <summary>Reads through whichever connection is current, so transfers survive a reconnection.</summary>
    private sealed class ConnectionFiles : IRemoteFiles
    {
        private volatile ServerConnection? _current;

        public ServerConnection? Current
        {
            get => _current;
            set => _current = value;
        }

        public Task<Stream> OpenReadAsync(RemoteFile file, long offset, long length, CancellationToken cancel) =>
            _current is { } connection
                ? connection.Files.OpenReadAsync(file, offset, length, cancel)
                : throw new StageException(Stage.Transfer, "Not connected to the server.", isRetryable: true);
    }
}
