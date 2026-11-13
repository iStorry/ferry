using Ferry.Core.Tools;

namespace Ferry.Core.Tests;

/// <summary>The one way external tools are run, against the shell this OS has.</summary>
public sealed class ToolProcessTests
{
    [Fact]
    public async Task AToolThatIsntThereIsSaidToBeMissing()
    {
        var e = await Assert.ThrowsAsync<StageException>(() => ToolProcess.RunAsync("ferry-no-such-tool", [], null, Stage.Download, CancellationToken.None, "Install it."));
        Assert.Equal(Stage.Download, e.Stage);
        Assert.Equal("ferry-no-such-tool isn't installed or isn't on PATH. Install it.", e.Message);
    }

    [Fact]
    public async Task OutputIsReadLineByLineAndAnErrorExitSaysWhatTheToolSaid()
    {
        var (shell, option) = OperatingSystem.IsWindows() ? ("cmd", "/c") : ("sh", "-c");
        var lines = new List<string>();
        await ToolProcess.RunAsync(shell, [option, "echo first && echo second"], lines.Add, Stage.Download, CancellationToken.None);
        Assert.Equal(["first", "second"], lines.Select(line => line.Trim()));

        var e = await Assert.ThrowsAsync<StageException>(() => ToolProcess.RunAsync(shell, [option, "echo it went wrong 1>&2 && exit 3"], null, Stage.Resolve, CancellationToken.None));
        Assert.Equal(Stage.Resolve, e.Stage);
        Assert.StartsWith($"{shell} stopped with code 3: it went wrong", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTakesTheToolDown()
    {
        var (shell, option, wait) = OperatingSystem.IsWindows() ? ("cmd", "/c", "ping -n 30 127.0.0.1 > nul") : ("sh", "-c", "sleep 30");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolProcess.RunAsync(shell, [option, wait], null, Stage.Download, cancel.Token));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10), "the tool was left running");
    }
}
