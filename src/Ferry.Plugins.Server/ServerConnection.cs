using Ferry.Core.Files;
using Ferry.Core;
using System.Globalization;
using Ferry.Core.Settings;

namespace Ferry.Plugins.Server;

/// <summary>The server to log in to; the login itself is a key file (<see cref="OpenSshLogin"/>).</summary>
/// <param name="HostKey">the fingerprint confirmed at setup ("SHA256:..."), or null when connecting the first time</param>
public sealed record ServerProfile(string Host, int Port, string User, string? HostKey)
{
    public static ServerProfile From(FerrySettings settings) =>
        new(settings.Text(ServerKeys.ServerHost), settings.Number(ServerKeys.ServerPort), settings.Text(ServerKeys.ServerUser),
            settings.Text(ServerKeys.ServerHostKey) is { Length: > 0 } key ? key : null);

    public bool IsSet => Host.Length > 0 && User.Length > 0;

    /// <summary>"user@host", with ":port" when it isn't 22.</summary>
    public string Display => $"{User}@{Host}{(Port == 22 ? "" : $":{Port}")}";

    /// <summary>Two fingerprints are the same key, however they were written ("SHA256:" prefix, base64 padding).</summary>
    public static bool SameKey(string a, string b) => string.Equals(Normalise(a), Normalise(b), StringComparison.Ordinal);

    private static string Normalise(string fingerprint)
    {
        var bare = fingerprint.Trim();
        if (bare.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            bare = bare[7..];
        return bare.TrimEnd('=');
    }
}

/// <summary>
/// The server answered with a different host key than the one confirmed at setup: the connection may be
/// intercepted, or the server was reinstalled. Nothing is sent until the new key is confirmed.
/// </summary>
public sealed class HostKeyChangedException(string server, string expected, string offered)
    : Exception($"{server} answered with a different host key ({offered}) than the one confirmed at setup ({expected}). " +
                "Someone may be intercepting the connection, or the server was reinstalled. Nothing was sent; if the change " +
                "is expected, run the server setup again.")
{
    public string Expected { get; } = expected;
    public string Offered { get; } = offered;
}

/// <summary>
/// The server, reached with the OpenSSH client (docs/openssh-plan.md §3): every command, the agent's attach channel,
/// an upload and each read of a file is its own run of ssh, with the same options, checked against Ferry's own
/// known_hosts. Nothing is held open between runs (Windows' OpenSSH can't share a connection), so creating one costs
/// nothing and the first command is the first login.
/// </summary>
public sealed class ServerConnection : IAsyncDisposable
{
    /// <summary>The most ssh lanes one file is read over at once.</summary>
    public const int MaxLanes = 8;

    private static readonly TimeSpan CommandLimit = TimeSpan.FromMinutes(5);

    private readonly SshTools _tools;
    private readonly ServerProfile _profile;
    private readonly SshTarget _target;
    private readonly SshLogin _login;
    private readonly string? _secret; // a key's passphrase, or setup's password: answered through askpass

    private ServerConnection(SshTools tools, ServerProfile profile, SshTarget target, SshLogin login, string? secret)
    {
        (_tools, _profile, _target, _login, _secret) = (tools, profile, target, login, secret);
    }

    /// <summary>The fingerprint of the server's key that every run is checked against ("SHA256:...").</summary>
    public string HostKey => _profile.HostKey ?? "";

    public string Display => _profile.Display;

    /// <summary>The server's files, each read over ssh lanes of its own.</summary>
    public IRemoteFiles Files => new SshFiles(this);

    /// <summary>The server set up in <paramref name="settings"/>, with its saved login.</summary>
    /// <exception cref="StageException">no server set up, no usable login, no OpenSSH client, or the server can't be
    /// reached to check its key the first time (<see cref="Stage.Server"/>)</exception>
    /// <exception cref="HostKeyChangedException">the server offers another key than the one confirmed at setup</exception>
    public static async Task<ServerConnection> OpenAsync(FerrySettings settings, SecretStore secrets, CancellationToken cancel)
    {
        var profile = ServerProfile.From(settings);
        if (!profile.IsSet)
            throw new StageException(Stage.Server, "No server is set up yet: run the server setup ('ferry server setup').");
        if (profile.HostKey is null)
            throw new StageException(Stage.Server, $"The host key of {profile.Display} was never confirmed; run the server setup again.");
        var paths = OpenSshPaths.For(secrets);
        var login = OpenSshLogin.Prepare(settings, secrets, paths);
        var tools = await SshTools.FindAsync(settings.Text(ServerKeys.SshPath), cancel);
        await KnownHosts.EnsureAsync(tools, profile, paths.KnownHosts, cancel);
        return WithKey(tools, profile, login, paths.KnownHosts, settings.Duration(ServerKeys.ServerKeepalive));
    }

    /// <summary>Log in with a key file; <paramref name="knownHosts"/> must hold the server's confirmed key.</summary>
    public static ServerConnection WithKey(SshTools tools, ServerProfile profile, OpenSshLogin login, string knownHosts, TimeSpan keepalive) =>
        new(tools, profile, Target(profile, login.KeyFile, knownHosts, keepalive), SshLogin.Key, login.Passphrase);

    /// <summary>Log in with a password (setup only, to add Ferry's key); <paramref name="knownHosts"/> must hold the
    /// server's confirmed key.</summary>
    public static ServerConnection WithPassword(SshTools tools, ServerProfile profile, string password, string knownHosts, TimeSpan keepalive) =>
        new(tools, profile, Target(profile, "", knownHosts, keepalive), SshLogin.Password, password);

    /// <summary>Run a shell command on the server and return what it printed.</summary>
    /// <exception cref="StageException">it failed (its stderr says why), or ssh did (unreachable, login refused)</exception>
    /// <exception cref="HostKeyChangedException">the server's key is not the confirmed one</exception>
    public Task<string> RunAsync(string command, CancellationToken cancel = default) => RunAsync(command, null, cancel);

    /// <inheritdoc cref="RunAsync(string, CancellationToken)"/>
    /// <param name="input">copied to the command's stdin, which is then closed</param>
    public async Task<string> RunAsync(string command, Stream? input, CancellationToken cancel)
    {
        // an upload takes as long as it takes; anything else that hasn't ended by now won't (a hung disk, a startup file holding the output open)
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (input is null)
            limit.CancelAfter(CommandLimit);
        SshResult result;
        try
        {
            await using var askpass = _secret is null ? null : Askpass.Start(_secret);
            result = await SshProcess.RunAsync(_tools.Ssh, Arguments(command), input, limit.Token, askpass?.Environment);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new StageException(Stage.Server, $"{Display} didn't finish what it was asked within {CommandLimit.TotalMinutes:0} minutes.", isRetryable: true);
        }
        if (result.ExitCode == SshErrors.SshFailed)
            throw await FailureAsync(result.Errors, cancel);
        if (result.ExitCode != 0)
            throw SshErrors.CommandFailed(Display, result.ExitCode, result.Errors);
        return AfterMarkerStream.TextAfter(result.Output);
    }

    /// <summary>Start <paramref name="command"/> and talk to it over its stdin and stdout: the agent's attach channel.
    /// It ends when ssh does: the command ended, or the connection died (three keepalives unanswered).</summary>
    /// <exception cref="StageException">ssh could not be started</exception>
    public AgentChannel OpenChannel(string command)
    {
        var askpass = _secret is null ? null : Askpass.Start(_secret);
        try
        {
            return new AgentChannel(SshProcess.Start(_tools.Ssh, Arguments(command), askpass?.Environment), askpass);
        }
        catch
        {
            askpass?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>Start <paramref name="command"/> for its stdout alone (a file's bytes), its stdin closed.</summary>
    internal (SshProcess Process, Askpass? Askpass) StartReading(string command)
    {
        var askpass = _secret is null ? null : Askpass.Start(_secret);
        var process = SshProcess.Start(_tools.Ssh, Arguments(command), askpass?.Environment);
        process.CloseInput();
        return (process, askpass);
    }

    /// <summary>Copy a local file to <paramref name="remotePath"/> on the server, over ssh's stdin (private to the user).</summary>
    public async Task UploadAsync(string localPath, string remotePath, CancellationToken cancel)
    {
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await RunAsync($"umask 077 && cat > {AgentInstaller.Quote(remotePath)}", file, cancel);
    }

    /// <summary>Nothing is held open between runs.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The exception for a failure of ssh's own; for a changed host key, the key the server offers now.</summary>
    internal async Task<Exception> FailureAsync(string errors, CancellationToken cancel)
    {
        var failure = SshErrors.Classify(errors);
        if (failure.Kind == SshFailureKind.LoginRefused && _login == SshLogin.Password)
        {
            return new StageException(Stage.Server, $"{Display} didn't accept that user and password. On a shared host or seedbox the SSH password is often not " +
                                                    $"the one for its website. If you have a key for it, choose A key file instead. ({failure.Detail})");
        }
        string? offered = null;
        if (failure.Kind == SshFailureKind.HostKeyChanged)
        {
            try
            {
                offered = (await KnownHosts.RecordAsync(_tools, _profile, cancel)).Fingerprint;
            }
            catch (StageException)
            {
                // the warning stands without it
            }
        }
        return SshErrors.ToException(failure, Display, _profile.HostKey, offered);
    }

    private List<string> Arguments(string command) => SshArguments.For(_target, _login, OnServer(command), askpass: _secret is not null);

    /// <summary>
    /// <paramref name="command"/> as the server's login shell is given it: run by sh, whatever that shell is (bash, zsh, fish, csh), after a
    /// marker that says its output starts here, whatever the shell's startup files printed (<see cref="AfterMarkerStream"/>).
    /// </summary>
    internal static string OnServer(string command) =>
        "sh -c " + AgentInstaller.Quote($"printf '%s\\n' '{AfterMarkerStream.Marker}'; {command}");

    private static SshTarget Target(ServerProfile profile, string keyFile, string knownHosts, TimeSpan keepalive)
    {
        // ssh checks exactly the confirmed key's type
        var type = profile.HostKey is { } confirmed
            ? KnownHosts.Read(knownHosts).FirstOrDefault(k => ServerProfile.SameKey(k.Fingerprint, confirmed))?.Type
            : null;
        return new SshTarget(profile.Host, profile.Port, profile.User, keyFile, knownHosts, type, keepalive);
    }
}

/// <summary>A command running on the server over ssh, talked to over its stdin and stdout.</summary>
public sealed class AgentChannel : IAsyncDisposable
{
    private readonly SshProcess _process;
    private readonly Askpass? _askpass;

    internal AgentChannel(SshProcess process, Askpass? askpass)
    {
        (_process, _askpass) = (process, askpass);
        FromAgent = new AfterMarkerStream(process.Output);
    }

    public Stream FromAgent { get; }

    /// <summary>What ssh or the command has said on stderr so far: why a channel ended, when it did.</summary>
    public string Errors => _process.Errors;
    public Stream ToAgent => _process.Input;

    /// <summary>Completes when ssh ends.</summary>
    public Task Running => _process.Exited;

    /// <summary>End of input (the agent ends the session), then ssh is given a few seconds before it is killed.</summary>
    public async ValueTask DisposeAsync()
    {
        await _process.DisposeAsync();
        if (_askpass is not null)
            await _askpass.DisposeAsync();
    }
}

/// <summary>
/// The server's finished files, each part read over an ssh of its own by the agent itself (<c>agent read</c>,
/// docs/openssh-plan.md §5): the PC names the item, never a path, and the agent serves only a file it finished.
/// </summary>
internal sealed class SshFiles(ServerConnection connection) : IRemoteFiles
{
    // sshd drops logins past MaxStartups (10 at once by default): parts start a few at a time, each holding its place
    // until its first bytes arrive (it has logged in) or it ends
    private static readonly SemaphoreSlim Handshakes = new(4, 4);

    public async Task<Stream> OpenReadAsync(RemoteFile file, long offset, long length, CancellationToken cancel)
    {
        var command = string.Create(CultureInfo.InvariantCulture, $"{AgentPaths.ShellCurrentAgent} agent read {file.Item:D} {offset} {length}");
        await Handshakes.WaitAsync(cancel);
        try
        {
            var (process, askpass) = connection.StartReading(command);
            return new SshStream(connection, process, askpass, Handshakes);
        }
        catch (StageException e)
        {
            Handshakes.Release();
            throw new StageException(Stage.Transfer, $"Could not read the file on the server: {e.Message}", isRetryable: true, inner: e);
        }
    }

    /// <summary>ssh's stdout; at its end, a failure of ssh or of the read becomes an exception, and disposing kills ssh
    /// at once (a reader that stops early doesn't want the rest).</summary>
    private sealed class SshStream(ServerConnection connection, SshProcess process, Askpass? askpass, SemaphoreSlim handshakes) : Stream
    {
        private readonly AfterMarkerStream _output = new(process.Output);
        private int _closed;
        private int _loggedIn; // its handshake place is given back once

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            int read;
            try
            {
                // a pipe read doesn't heed the token once it waits: a stall or a pause kills ssh, which ends it
                using (cancel.Register(static state => ((SshProcess)state!).Kill(), process))
                    read = await _output.ReadAsync(buffer, cancel);
            }
            catch (IOException) when (cancel.IsCancellationRequested)
            {
                read = 0;
            }
            cancel.ThrowIfCancellationRequested();
            LoggedIn();
            if (read > 0)
                return read;
            await process.Exited.WaitAsync(cancel);
            if (process.ExitCode == 0)
                return 0;
            await process.ErrorsEndedAsync(cancel); // or the message below may be missing what it should say
            if (process.ExitCode != SshErrors.SshFailed)
            {
                // the agent refused (not finished, gone, outside its folder): its message says why
                throw new StageException(Stage.Transfer, $"The file can't be read on the server: {process.Errors.Trim()}");
            }
            var failure = await connection.FailureAsync(process.Errors, cancel);
            // the connection dropped: the transfer tries again from where it got to
            throw failure is StageException { IsRetryable: true } dropped
                ? new StageException(Stage.Transfer, $"Could not read the file on the server: {dropped.Message}", isRetryable: true, inner: dropped)
                : failure;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
                await CloseAsync();
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
                CloseAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        private void LoggedIn()
        {
            if (Interlocked.Exchange(ref _loggedIn, 1) == 0)
                handshakes.Release();
        }

        private async ValueTask CloseAsync()
        {
            LoggedIn();
            process.Kill();
            await process.DisposeAsync();
            if (askpass is not null)
                await askpass.DisposeAsync();
        }
    }
}
