using System.Diagnostics;
using System.Text;
using Ferry.Core;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>
/// The process side of the OpenSSH layer against an ordinary shell standing in for ssh, and the small parsers the
/// agent and the probe use; none of it needs a server.
/// </summary>
public sealed class SshProcessTests
{
    private static (string Program, string[] Arguments) Shell(string windows, string unix) =>
        OperatingSystem.IsWindows() ? ("cmd.exe", ["/d", "/c", windows]) : ("/bin/sh", ["-c", unix]);

    [Fact]
    public async Task ARunGivesItsOutputExitCodeAndTheEndOfItsErrors()
    {
        var (program, arguments) = Shell("echo hello& echo oops 1>&2& exit 3", "echo hello; echo oops >&2; exit 3");

        var result = await SshProcess.RunAsync(program, arguments, null, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("hello", result.Output.Trim());
        Assert.Equal("oops", result.Errors.Trim());
    }

    [Fact]
    public async Task InputGoesToItsStdinWhichThenCloses()
    {
        var (program, arguments) = Shell("sort", "cat"); // both read stdin to its end; these lines are in order already
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("line one\nline two\n"));

        var result = await SshProcess.RunAsync(program, arguments, input, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["line one", "line two"], result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task DisposingKillsAProcessThatWontEnd()
    {
        var (program, arguments) = Shell("ping -n 60 127.0.0.1 >nul", "sleep 60");
        var ssh = SshProcess.Start(program, arguments);
        var clock = Stopwatch.StartNew();

        await ssh.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(ssh.Exited.IsCompleted);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(15)); // a few seconds' grace, then killed
    }

    [Fact]
    public async Task DisposingAfterItsInputWasClosedElsewhereIsFine()
    {
        // the agent's client closes ssh's stdin stream itself; the channel then disposes the process
        var (program, arguments) = Shell("sort", "cat");
        var ssh = SshProcess.Start(program, arguments);
        await ssh.Input.DisposeAsync();

        await ssh.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(ssh.Exited.IsCompleted);
    }

    [Fact]
    public async Task CancellingARunKillsItAtOnce()
    {
        var (program, arguments) = Shell("ping -n 60 127.0.0.1 >nul", "sleep 60");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SshProcess.RunAsync(program, arguments, null, cancel.Token));

        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OnlyTheEndOfALongStderrIsKept()
    {
        var (program, arguments) = Shell(
            "for /L %i in (1,1,2000) do @echo line %i 1>&2",
            "i=1; while [ $i -le 2000 ]; do echo \"line $i\" >&2; i=$((i+1)); done");

        var result = await SshProcess.RunAsync(program, arguments, null, CancellationToken.None);

        Assert.True(Encoding.UTF8.GetByteCount(result.Errors) <= 8 << 10);
        Assert.EndsWith("line 2000", result.Errors.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyLoginWithAPassphraseLetsAskpassAnswer()
    {
        var target = new SshTarget("host", 22, "u", "/k", "/kh", "ssh-ed25519", TimeSpan.FromSeconds(30));

        string Option(List<string> arguments, string name) =>
            arguments[arguments.IndexOf(arguments.First(a => a.StartsWith(name + "=", StringComparison.Ordinal)))][(name.Length + 1)..];

        Assert.Equal("yes", Option(SshArguments.For(target, SshLogin.Key, "true"), "BatchMode"));
        var withPassphrase = SshArguments.For(target, SshLogin.Key, "true", askpass: true);
        Assert.Equal("no", Option(withPassphrase, "BatchMode"));
        Assert.Equal("1", Option(withPassphrase, "NumberOfPasswordPrompts"));
        Assert.Equal("yes", Option(withPassphrase, "StrictHostKeyChecking")); // still never asks about the host
    }

    [Fact]
    public void TheProbeSaysHowLongEachAttachHasRun()
    {
        var facts = AgentFacts.Parse("""
            home=/home/al
            uname=Linux x86_64
            current=ferry-0123456789abcdef
            sha256=0123456789abcdef0000
            running=/home/al/.ferry/bin/ferry-fedcba9876543210
            attach=12
            attach=5400
            attach=not-a-number
            """);

        Assert.Equal([TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(5400)], facts.AttachAges);
        Assert.True(facts.RunsOtherThan("ferry-0123456789abcdef"));
        Assert.False(facts.RunsOtherThan("ferry-fedcba9876543210"));
        // an agent still running from a file that was replaced counts as another build: it is restarted
        Assert.True(AgentFacts.Parse("home=/h\nrunning=/h/.ferry/bin/ferry-aa (deleted)").RunsOtherThan("ferry-aa"));
        Assert.Empty(AgentFacts.Parse("home=/h").AttachAges);
    }

    [Fact]
    public void MemoryAndLoadAreReadFromProc()
    {
        var (total, available) = AgentStats.Memory("MemTotal:       528085204 kB\nMemFree:          4228432 kB\nMemAvailable:   411858116 kB\n");

        Assert.Equal(528085204L * 1024, total);
        Assert.Equal(411858116L * 1024, available);
        Assert.Equal((0.82, 0.75, 0.70), AgentStats.Load("0.82 0.75 0.70 3/2412 96881\n"));
        Assert.Null(AgentStats.Load(null));
        Assert.Equal((null, null), AgentStats.Memory(null));
    }

    [Theory]
    [InlineData("1.0.0+b0f6d6255ce73d890696bb70496d255b61acf8e3", "1.0.0 (b0f6d62)")]
    [InlineData("1.0.0+abc", "1.0.0 (abc)")]
    [InlineData("1.0.0", "1.0.0")]
    public void BuildsAreShownShort(string build, string shown) => Assert.Equal(shown, Product.Describe(build));
}
