using Ferry.Core;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>The OpenSSH layer's parts that need no ssh: finding it, its arguments, and reading its failures.</summary>
public sealed class OpenSshTests
{
    private static readonly SshTarget Target = new("server.example", 2222, "al", @"C:\Users\Al\AppData\Local\Ferry\ssh\ferry_ed25519",
                                                   @"C:\Users\Al\AppData\Local\Ferry\known_hosts", "ssh-ed25519", TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData("OpenSSH_for_Windows_9.5p2, LibreSSL 3.8.2", 9, 5)]
    [InlineData("OpenSSH_for_Windows_8.1p1, LibreSSL 3.0.2", 8, 1)]
    [InlineData("OpenSSH_10.0p2 Debian-7+deb13u4, OpenSSL 3.5.1 1 Jul 2025", 10, 0)]
    [InlineData("OpenSSH_9.6p1 Ubuntu-3ubuntu13.5, OpenSSL 3.0.13 30 Jan 2024", 9, 6)]
    public void VersionIsReadFromWhatSshSays(string text, int major, int minor) =>
        Assert.Equal(new Version(major, minor), SshTools.ParseVersion(text));

    [Fact]
    public void SomethingElseIsNotOpenSsh()
    {
        Assert.Null(SshTools.ParseVersion("PuTTY Release 0.81"));
        Assert.Null(SshTools.ParseVersion(""));
    }

    [Fact]
    public void OnlyTheConfiguredClientIsTriedWhenOneIsSet()
    {
        var folder = Directory.CreateTempSubdirectory("ferry_ssh_").FullName;
        try
        {
            var ssh = Path.Combine(folder, OperatingSystem.IsWindows() ? "ssh.exe" : "ssh");
            Assert.Equal([ssh], SshTools.Candidates(folder));
            Assert.Equal([ssh], SshTools.Candidates($"\"{ssh}\""));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void TheCarriedClientComesFirstThenTheSystems()
    {
        var candidates = SshTools.Candidates(null).ToList();

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "openssh", OperatingSystem.IsWindows() ? "ssh.exe" : "ssh"), candidates[0]);
        if (OperatingSystem.IsWindows())
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"), candidates[^1]);
    }

    [Fact]
    public void EveryRunIgnoresTheUsersConfigAndEndsWithTheCommand()
    {
        var arguments = SshArguments.For(Target, SshLogin.Key, "'/home/al/.ferry/bin/current' agent attach");

        Assert.Equal(["-F", "none"], arguments.Take(2));
        Assert.Equal(["server.example", "--", "'/home/al/.ferry/bin/current' agent attach"], arguments.TakeLast(3));
        Assert.Equal("2222", After(arguments, "-p"));
        Assert.Equal("al", After(arguments, "-l"));
        Assert.Contains("-T", arguments);
        Assert.Equal("none", After(arguments, "-e"));
    }

    [Fact]
    public void AKeyLoginNeverAsksAndChecksTheConfirmedKey()
    {
        var options = Options(SshArguments.For(Target, SshLogin.Key, "true"));

        Assert.Equal("yes", options["BatchMode"]);
        Assert.Equal("yes", options["StrictHostKeyChecking"]);
        Assert.Equal("ssh-ed25519", options["HostKeyAlgorithms"]);
        Assert.Equal("yes", options["IdentitiesOnly"]);
        Assert.Equal("none", options["IdentityAgent"]);
        Assert.Equal("publickey", options["PreferredAuthentications"]);
        Assert.Equal("30", options["ServerAliveInterval"]);
        Assert.Equal("3", options["ServerAliveCountMax"]);
        Assert.StartsWith("aes128-gcm@openssh.com,", options["Ciphers"], StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingTheHostKeyOffersNoLogin()
    {
        var arguments = SshArguments.For(Target with { HostKeyType = null }, SshLogin.RecordHostKey, "true");
        var options = Options(arguments);

        Assert.DoesNotContain("-i", arguments);
        Assert.Equal("accept-new", options["StrictHostKeyChecking"]);
        foreach (var method in new[] { "PubkeyAuthentication", "PasswordAuthentication", "KbdInteractiveAuthentication",
                                       "GSSAPIAuthentication", "HostbasedAuthentication" })
            Assert.Equal("no", options[method]);
        Assert.False(options.ContainsKey("HostKeyAlgorithms"));
    }

    [Fact]
    public void APasswordLoginIsAnsweredByAskpassOnce()
    {
        var arguments = SshArguments.For(Target, SshLogin.Password, "true");
        var options = Options(arguments);

        Assert.DoesNotContain("-i", arguments);
        Assert.Equal("no", options["BatchMode"]);
        Assert.Equal("no", options["PubkeyAuthentication"]);
        Assert.Equal("1", options["NumberOfPasswordPrompts"]);
        Assert.Equal("yes", options["StrictHostKeyChecking"]);
    }

    [Fact]
    public void AnRsaHostKeyIsCheckedWithSha2Signatures() =>
        Assert.Equal("rsa-sha2-512,rsa-sha2-256", SshArguments.HostKeyAlgorithmsFor("ssh-rsa"));

    [Fact]
    public void PathsSurviveSshsOwnParsing()
    {
        var spaced = OperatingSystem.IsWindows() ? @"C:\Users\Al Smith\known_hosts" : "/home/al smith/known_hosts";

        // "%" doubled (ssh expands %x), backslashes made forward on Windows, a space quoted in a config value
        Assert.Equal(OperatingSystem.IsWindows() ? "C:/Users/a/100%%/key" : "/home/a/100%%/key",
                     SshArguments.FilePath(OperatingSystem.IsWindows() ? @"C:\Users\a\100%\key" : "/home/a/100%/key"));
        Assert.Equal(OperatingSystem.IsWindows() ? "\"C:/Users/Al Smith/known_hosts\"" : "\"/home/al smith/known_hosts\"",
                     SshArguments.ConfigPath(spaced));
        Assert.Equal(OperatingSystem.IsWindows() ? "C:/Users/a/known_hosts" : "/home/a/known_hosts",
                     SshArguments.ConfigPath(OperatingSystem.IsWindows() ? @"C:\Users\a\known_hosts" : "/home/a/known_hosts"));
        Assert.Throws<ArgumentException>(() => SshArguments.FilePath("/home/${USER}/key"));
    }

    [Theory]
    // Windows 9.5p2 and Linux, as they print them
    [InlineData("@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\r\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\r\n" +
                "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\r\nIT IS POSSIBLE THAT SOMEONE IS DOING SOMETHING NASTY!\r\n" +
                "Host key for server.example has changed and you have requested strict checking.\r\nHost key verification failed.\r\n",
                "HostKeyChanged")]
    [InlineData("No ED25519 host key is known for server.example and you have requested strict checking.\r\nHost key verification failed.\r\n",
                "HostKeyUnknown")]
    [InlineData("al@server.example: Permission denied (publickey).\r\n", "LoginRefused")]
    [InlineData("Received disconnect from 1.2.3.4 port 22:2: Too many authentication failures\n", "LoginRefused")]
    [InlineData("Bad permissions. Try removing permissions for user: BUILTIN\\Users (S-1-5-32-545) on file C:/key.\r\n" +
                "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\r\n@         WARNING: UNPROTECTED PRIVATE KEY FILE!          @\r\n", "KeyFile")]
    [InlineData("Load key \"C:/key\": invalid format\r\nal@server.example: Permission denied (publickey).\r\n", "KeyFile")]
    [InlineData("ssh: Could not resolve hostname server.example: No such host is known. \r\n", "Unreachable")]
    [InlineData("ssh: connect to host server.example port 22: Connection refused\n", "Unreachable")]
    [InlineData("ssh: connect to host server.example port 22: Connection timed out\r\n", "Unreachable")]
    [InlineData("Timeout, server server.example not responding.\r\n", "Unreachable")]
    [InlineData("client_loop: send disconnect: Connection reset\r\n", "Unreachable")]
    [InlineData("kex_exchange_identification: read: Connection reset by peer\n", "Unreachable")]
    [InlineData("something new went wrong\n", "Other")]
    public void FailuresAreReadFromWhatSshPrints(string errors, string kind) =>
        Assert.Equal(kind, SshErrors.Classify(errors).Kind.ToString());

    [Fact]
    public void FailuresBecomeWhatServerModeExpects()
    {
        Assert.IsType<HostKeyChangedException>(
            SshErrors.ToException(SshErrors.Classify("Host key verification failed."), "al@midas", "SHA256:a", "SHA256:b"));
        var unreachable = Assert.IsType<StageException>(
            SshErrors.ToException(SshErrors.Classify("ssh: connect to host midas port 22: Connection refused"), "al@midas", null));
        Assert.True(unreachable.IsRetryable);
        Assert.Equal(Stage.Server, unreachable.Stage);
        Assert.Contains("Connection refused", unreachable.Message, StringComparison.Ordinal);
        var refused = Assert.IsType<StageException>(
            SshErrors.ToException(SshErrors.Classify("al@midas: Permission denied (publickey)."), "al@midas", null));
        Assert.False(refused.IsRetryable);
    }

    [Fact]
    public void ARefusedLoginSaysWhyAskpassHadNoAnswer()
    {
        var failure = SshErrors.Classify("ferry askpass: Ferry's answer couldn't be had (IOException: refused).\n" +
                                         "al@midas: Permission denied (publickey,password).\n");

        Assert.Equal(SshFailureKind.LoginRefused, failure.Kind);
        Assert.Contains("Permission denied", failure.Detail, StringComparison.Ordinal);
        Assert.Contains("ferry askpass: Ferry's answer couldn't be had", failure.Detail, StringComparison.Ordinal);

        // whatever else went wrong on the way (the answering program dying, say) is kept too
        var crashed = SshErrors.Classify("Unhandled exception. System.IO.FileNotFoundException: hook.dll\njat@midas: Permission denied (password).\n");
        Assert.Contains("Unhandled exception", crashed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetailIsOneCleanLine()
    {
        var failure = SshErrors.Classify("debug noise\r\nssh: connect to host midas port 22: Connection refused\u0007\r\n" + new string('x', 500));

        Assert.Equal("ssh: connect to host midas port 22: Connection refused", failure.Detail);
    }

    private static string After(List<string> arguments, string flag) => arguments[arguments.IndexOf(flag) + 1];

    /// <summary>The -o options, name → value.</summary>
    private static Dictionary<string, string> Options(List<string> arguments) =>
        arguments.Select((a, i) => (a, i))
                 .Where(x => x.a == "-o")
                 .Select(x => arguments[x.i + 1].Split('=', 2))
                 .ToDictionary(p => p[0], p => p[1]);
}
