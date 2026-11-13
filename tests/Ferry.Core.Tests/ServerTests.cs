using System.Net;
using System.Net.Sockets;
using System.Text;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Settings;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>Server mode's SSH side that needs no server: secrets, keys, OS detection, quoting, failures.</summary>
public sealed class ServerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ferry_server_").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void SecretsRoundTripAndAreNotPlainOnWindows()
    {
        var secrets = new SecretStore(Path.Combine(_root, "secrets"));
        var secret = Encoding.UTF8.GetBytes("hunter2-and-a-key");

        secrets.Save("login", secret);

        Assert.Equal(secret, secrets.Load("login"));
        var file = Directory.GetFiles(Path.Combine(_root, "secrets")).Single();
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain("hunter2", Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        else
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        secrets.Delete("login");
        Assert.Null(secrets.Load("login"));
    }

    [Fact]
    public void ForgettingOnANewInstallIsFine()
    {
        // the end of a first setup with a password: nothing was ever saved, not even the secrets folder
        var secrets = new SecretStore(Path.Combine(_root, "never-made", "secrets"));

        ServerLogin.Forget(secrets);

        Assert.Null(ServerLogin.Load(secrets));
    }

    [Fact]
    public void LoginIsSavedButNeverPrinted()
    {
        var secrets = new SecretStore(Path.Combine(_root, "secrets"));
        var login = new ServerLogin(PrivateKey: "-----BEGIN PRIVATE KEY-----secret", Passphrase: "pass phrase");

        login.Save(secrets);

        Assert.Equal(login, ServerLogin.Load(secrets));
        Assert.Equal("key with passphrase", login.ToString());
        Assert.Equal("password", new ServerLogin(Password: "pw").ToString());
        ServerLogin.Forget(secrets);
        Assert.Null(ServerLogin.Load(secrets));
    }

    [Fact]
    public async Task GeneratedKeyIsAnEd25519KeyOpenSshCanUse()
    {
        var tools = await SshTools.FindAsync(null, CancellationToken.None); // the machine's OpenSSH client
        var path = Path.Combine(_root, "ssh", "ferry_ed25519");

        var authorizedKey = await SshKeyFiles.CreateAsync(tools, path, "ferry@test", CancellationToken.None);

        var parts = authorizedKey.Split(' ');
        Assert.Equal(["ssh-ed25519", "ferry@test"], [parts[0], parts[2]]);
        Assert.True(PrivateFiles.IsPrivate(path));
        Assert.False(await SshKeyFiles.NeedsPassphraseAsync(tools, path, CancellationToken.None));
        var notAKey = Path.Combine(_root, "not-a-key");
        File.WriteAllText(notAKey, "not a key");
        PrivateFiles.Restrict(notAKey);
        await Assert.ThrowsAsync<StageException>(() => SshKeyFiles.NeedsPassphraseAsync(tools, notAKey, CancellationToken.None));
    }

    [Theory]
    [InlineData("Linux x86_64", false, "linux-x64")]
    [InlineData("Linux aarch64", false, "linux-arm64")]
    [InlineData("Linux x86_64", true, null)] // musl (Alpine): the agent is built for glibc
    [InlineData("Darwin arm64", false, null)]
    [InlineData("Linux armv7l", false, null)]
    [InlineData("", false, null)]
    public void ServerOsIsMatchedToAnAgentBuild(string uname, bool musl, string? rid) =>
        Assert.Equal(rid, AgentInstaller.RidFor(uname, musl));

    [Theory]
    [InlineData("/home/al/.ferry", "'/home/al/.ferry'")]
    [InlineData("/home/o'neil", "'/home/o'\\''neil'")]
    [InlineData("a b; rm -rf /", "'a b; rm -rf /'")]
    public void ShellArgumentsAreQuoted(string text, string quoted) => Assert.Equal(quoted, AgentInstaller.Quote(text));

    [Fact]
    public void AuthorizedKeyIsAddedOnceWithPrivatePermissions()
    {
        var command = ServerSetup.AuthorizeCommand("ecdsa-sha2-nistp256 AAAA ferry@pc");

        Assert.StartsWith("umask 077 && ", command, StringComparison.Ordinal);
        Assert.Contains("grep -qxF 'ecdsa-sha2-nistp256 AAAA ferry@pc'", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SHA256:abc+/x", "abc+/x", true)]
    [InlineData("SHA256:abc+/x=", "sha256:abc+/x", true)]
    [InlineData("SHA256:abc", "SHA256:abd", false)]
    public void HostKeysAreComparedHoweverWritten(string a, string b, bool same) => Assert.Equal(same, ServerProfile.SameKey(a, b));

    [Fact]
    public void AgentBuildIsFoundWhereChosen()
    {
        var build = Path.Combine(_root, "ferry-linux-x64");
        File.WriteAllText(build, "binary");

        Assert.Equal(build, AgentBinaries.Find("linux-x64", build));
        Assert.Null(AgentBinaries.Find("linux-x64", Path.Combine(_root, "missing")));
    }

    [Fact]
    public async Task ClosingWhileAWriteIsInFlightClosesBothEndsAndSaysNothing()
    {
        await using var input = new WatchedStream();
        await using var output = new WatchedStream();
        var lines = new AgentLines(input, output);
        var writing = lines.WriteAsync(new AgentRequest(1, AgentCommand.Ping), AgentJson.Default.AgentRequest, CancellationToken.None);
        await output.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await lines.DisposeAsync();

        Assert.True(input.Closed);
        Assert.True(output.Closed);
        output.LetWritesGo();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writing); // what the two ends take for the connection being gone
    }

    /// <summary>A stream that says when it is closed, whose writes wait to be let go.</summary>
    private sealed class WatchedStream : MemoryStream
    {
        private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Closed { get; private set; }

        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void LetWritesGo() => _go.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writing.TrySetResult();
            await _go.Task;
        }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task SessionNeedsASetUpServer()
    {
        var settings = FerrySettings.Defaults(new AppPaths(_root, _root));

        var failure = await Assert.ThrowsAsync<StageException>(() => ServerSession.OpenAsync(settings, new SecretStore(_root), CancellationToken.None));

        Assert.Equal(Stage.Server, failure.Stage);
        Assert.Contains("server setup", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableServerIsARetryableServerFailure()
    {
        var port = ClosedPort();
        var tools = await SshTools.FindAsync(null, CancellationToken.None); // the machine's OpenSSH client

        var failure = await Assert.ThrowsAsync<StageException>(() => KnownHosts.RecordAsync(
            tools, new ServerProfile("127.0.0.1", port, "nobody", null), CancellationToken.None));

        Assert.Equal(Stage.Server, failure.Stage);
        Assert.True(failure.IsRetryable);
    }

    internal static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port; // closed again once disposed
    }
}
