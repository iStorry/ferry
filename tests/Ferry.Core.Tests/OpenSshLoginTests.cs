using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Settings;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>OpenSSH's host keys, key files, askpass and the move from SSH.NET's saved login; none of it needs ssh.</summary>
public sealed class OpenSshLoginTests : IDisposable
{
    // midas.usbx.me's Ed25519 host key, and the fingerprint ssh-keygen -lf showed for it
    private const string MidasKey = "AAAAC3NzaC1lZDI1NTE5AAAAIIts71iQfmf8yeIq+GBZga+lH0MREulzZmUoBku79zVa";
    private const string MidasFingerprint = "SHA256:dIVa2T1/qdK9vNFLgL/83zWNb023RgZhiFw+YK1odW8";

    private static readonly SshTools NoSsh = new("no-ssh", "no-ssh-keygen", new Version(9, 5), "");
    private readonly string _root = Directory.CreateTempSubdirectory("ferry_openssh_").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private OpenSshPaths Paths => new(Path.Combine(_root, "config", "ssh"));

    private FerrySettings Settings(params (string Key, string Value)[] settings)
    {
        var store = new SettingsStore(new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "downloads")), new Dictionary<string, string>());
        foreach (var (key, value) in settings)
            store.Set(key, value);
        return store.Load();
    }

    private SecretStore Secrets => new(Path.Combine(_root, "config", "secrets"));

    [Fact]
    public void FingerprintIsTheOneSshShows() => Assert.Equal(MidasFingerprint, KnownHosts.Fingerprint(MidasKey));

    [Theory]
    [InlineData($"midas.usbx.me ssh-ed25519 {MidasKey}", "midas.usbx.me")]
    [InlineData($"[midas.usbx.me]:2222 ssh-ed25519 {MidasKey} a comment", "[midas.usbx.me]:2222")]
    public void KnownHostsLinesAreRead(string line, string hosts)
    {
        var key = HostKey.Parse(line)!;

        Assert.Equal(hosts, key.Hosts);
        Assert.Equal("ssh-ed25519", key.Type);
        Assert.Equal(MidasFingerprint, key.Fingerprint);
    }

    [Theory]
    [InlineData("# a comment")]
    [InlineData("")]
    [InlineData($"@revoked midas.usbx.me ssh-ed25519 {MidasKey}")]
    [InlineData("midas.usbx.me ssh-ed25519 not*base64")]
    [InlineData("midas.usbx.me ssh-ed25519")]
    public void OtherLinesAreSkipped(string line) => Assert.Null(HostKey.Parse(line));

    [Fact]
    public void KnownHostsHoldsTheConfirmedKeyAlonePrivately()
    {
        var path = Paths.KnownHosts;
        KnownHosts.Save(path, new HostKey("old.example", "ssh-ed25519", MidasKey));

        KnownHosts.Save(path, new HostKey("midas.usbx.me", "ssh-ed25519", MidasKey));

        Assert.Equal($"midas.usbx.me ssh-ed25519 {MidasKey}\n", File.ReadAllText(path));
        Assert.Equal("midas.usbx.me", Assert.Single(KnownHosts.Read(path)).Hosts);
        Assert.True(PrivateFiles.IsPrivate(path));
    }

    [Fact]
    public async Task AKnownConfirmedKeyNeedsNoConnection()
    {
        KnownHosts.Save(Paths.KnownHosts, new HostKey("midas.usbx.me", "ssh-ed25519", MidasKey));
        var profile = new ServerProfile("midas.usbx.me", 22, "al", MidasFingerprint);

        var key = await KnownHosts.EnsureAsync(NoSsh, profile, Paths.KnownHosts, CancellationToken.None); // ssh would fail: it isn't there

        Assert.Equal(MidasKey, key.Key);
    }

    [Fact]
    public async Task NoConfirmedKeyMeansSetupAgain()
    {
        var profile = new ServerProfile("midas.usbx.me", 22, "al", HostKey: null);

        var missing = await Assert.ThrowsAsync<StageException>(() => KnownHosts.EnsureAsync(NoSsh, profile, Paths.KnownHosts, CancellationToken.None));

        Assert.Contains("setup", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateFilesArePrivateFromTheFirstByte()
    {
        var path = Path.Combine(Paths.Folder, "secret.txt");

        PrivateFiles.WriteText(path, "hello");

        Assert.Equal("hello", File.ReadAllText(path));
        Assert.True(PrivateFiles.IsPrivate(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("al@midas.usbx.me's password: ", true)]
    [InlineData("Password: ", true)]
    [InlineData("Enter passphrase for key 'C:/Users/al/key': ", true)]
    [InlineData("newuser@newhost.example's password: ", true)]
    [InlineData("New password: ", false)]
    [InlineData("Retype new password: ", false)]
    [InlineData("Enter new passphrase: ", false)]
    [InlineData("Verification code: ", false)]
    [InlineData("Are you sure you want to continue connecting (yes/no/[fingerprint])? ", false)]
    public void OnlyAPasswordOrPassphrasePromptIsAnswered(string prompt, bool answered) =>
        Assert.Equal(answered, Askpass.AsksForSecret(prompt));

    [Fact]
    public async Task AskpassAnswersItsCallerThroughThePipe()
    {
        await using var askpass = Askpass.Start("hunter2", "ferry");
        var variable = askpass.Environment[Askpass.Variable];

        var (answer, problem) = await Askpass.AnswerAsync(variable, "al@midas's password: ", CancellationToken.None);

        Assert.Equal("hunter2", answer);
        Assert.Null(problem);
        Assert.Equal("force", askpass.Environment["SSH_ASKPASS_REQUIRE"]);
        Assert.DoesNotContain("hunter2", string.Join(' ', askpass.Environment.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskpassGivesNothingForAWrongTokenOrNoPipe()
    {
        await using var askpass = Askpass.Start("hunter2", "ferry");
        var name = askpass.Environment[Askpass.Variable]!.Split(';')[0];

        var wrong = await Askpass.AnswerAsync($"{name};wrong", "Password: ", CancellationToken.None);
        Assert.Null(wrong.Answer);
        Assert.Contains("no answer", wrong.Problem, StringComparison.Ordinal);
        Assert.Contains("isn't set", (await Askpass.AnswerAsync(null, "Password: ", CancellationToken.None)).Problem, StringComparison.Ordinal);
        Assert.Null((await Askpass.AnswerAsync("garbage", "Password: ", CancellationToken.None)).Answer);
    }

    [Fact]
    public void ASavedKeyBecomesAPrivateFile()
    {
        new ServerLogin(PrivateKey: "-----BEGIN PRIVATE KEY-----\r\nabc\r\n-----END PRIVATE KEY-----", Passphrase: "pp").Save(Secrets);

        var login = OpenSshLogin.Prepare(Settings(), Secrets, Paths);

        Assert.Equal(Paths.MigratedKey, login.KeyFile);
        Assert.Equal("pp", login.Passphrase);
        Assert.Equal("-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----\n", File.ReadAllText(login.KeyFile));
        Assert.True(PrivateFiles.IsPrivate(login.KeyFile));
        Assert.NotNull(ServerLogin.Load(Secrets)!.PrivateKey); // kept until a connection with the file has worked

        OpenSshLogin.CompleteMigration(Secrets);

        Assert.Equal(new ServerLogin(Passphrase: "pp"), ServerLogin.Load(Secrets));
        Assert.Equal(login, OpenSshLogin.Prepare(Settings(), Secrets, Paths)); // found by its name now
    }

    [Fact]
    public void AKeyWithoutPassphraseLeavesNoSecretBehind()
    {
        new ServerLogin(PrivateKey: "-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----\n").Save(Secrets);

        OpenSshLogin.Prepare(Settings(), Secrets, Paths);
        OpenSshLogin.CompleteMigration(Secrets);

        Assert.Null(ServerLogin.Load(Secrets));
        Assert.Equal(Paths.MigratedKey, OpenSshLogin.Prepare(Settings(), Secrets, Paths).KeyFile);
    }

    [Fact]
    public void TheKeyTheSetupChoseComesFirst()
    {
        var chosen = Path.Combine(_root, "id_ed25519");
        File.WriteAllText(chosen, "key");
        new ServerLogin(PrivateKey: "-----BEGIN PRIVATE KEY-----\nold\n-----END PRIVATE KEY-----\n").Save(Secrets);

        Assert.Equal(chosen, OpenSshLogin.Prepare(Settings((ServerKeys.ServerKey.Name, chosen)), Secrets, Paths).KeyFile);
        Assert.False(File.Exists(Paths.MigratedKey));
    }

    [Fact]
    public void ASavedPasswordOrPuttyKeyMeansSetupAgain()
    {
        new ServerLogin(Password: "pw").Save(Secrets);
        Assert.Contains("no longer keeps a password", Assert.Throws<StageException>(() => OpenSshLogin.Prepare(Settings(), Secrets, Paths)).Message,
                        StringComparison.Ordinal);

        new ServerLogin(PrivateKey: "PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none").Save(Secrets);
        Assert.Contains("PuTTY", Assert.Throws<StageException>(() => OpenSshLogin.Prepare(Settings(), Secrets, Paths)).Message, StringComparison.Ordinal);

        ServerLogin.Forget(Secrets);
        Assert.Throws<StageException>(() => OpenSshLogin.Prepare(Settings(), Secrets, Paths));
    }

    [Fact]
    public void AKeyFileThatIsGoneMeansSetupAgain()
    {
        var settings = Settings((ServerKeys.ServerKey.Name, Path.Combine(_root, "gone")));

        Assert.Contains("gone", Assert.Throws<StageException>(() => OpenSshLogin.Prepare(settings, Secrets, Paths)).Message, StringComparison.Ordinal);
    }
}
