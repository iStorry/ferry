using System.CommandLine;
using Ferry.Agent.Console;
using Ferry.Core;
using Ferry.Core.Settings;
using Ferry.Plugins.BrowserLink;
using Ferry.Plugins.Official;
using Ferry.Plugins.Server;

namespace Ferry.Agent;

/// <summary>
/// What this program is run for, none of it by hand: a server's agent, ssh's password helper, and the host a browser's
/// extension talks to.
/// </summary>
internal static class Commands
{
    public static async Task<int> RunAsync(string[] args, AgentConsole console, CancellationToken cancel)
    {
        if (BrowserHost.IsStartedFor(args))
        {
            await using var input = System.Console.OpenStandardInput();
            await using var output = System.Console.OpenStandardOutput();
            return await BrowserHost.RunAsync(args, input, output, Path.Combine(AppContext.BaseDirectory, "..", "Ferry.exe"), cancel);
        }
#if SERVER_AGENT
        OfficialSites.Register(Ferry.Plugins.Torrents.TorrentsPlugin.Plugin);
#else
        OfficialSites.Register();
#endif
        ServerKeys.Register();
        var root = new RootCommand($"{Product.Name}'s helper: the app runs it, on this PC and on your server.")
        {
            AgentCommands.Create(console),
            AskpassCommand.Create(console),
        };
        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var error in parsed.Errors)
                console.Error.WriteLine(error.Message);
            return ExitCodes.Usage;
        }
        var invocation = new InvocationConfiguration
        {
            Output = console.Out,
            Error = console.Error,
            ProcessTerminationTimeout = null, // Program handles Ctrl+C
            EnableDefaultExceptionHandler = false,
        };
        try
        {
            return await parsed.InvokeAsync(invocation, cancel);
        }
        catch (Exception e) when (e is SettingsException or StageException)
        {
            console.Error.WriteLine(e.Message);
            return ExitCodes.Failed;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return ExitCodes.Interrupted;
        }
#pragma warning disable CA1031 // the last resort: a message and an exit code, not a stack trace
        catch (Exception e)
#pragma warning restore CA1031
        {
            console.Error.WriteLine($"Something went wrong: {e.Message}");
            return ExitCodes.Failed;
        }
    }

    /// <summary>
    /// ssh runs SSH_ASKPASS with the prompt as its only argument ("ferry 'al@host's password: '"), never with a
    /// command. With <see cref="Askpass.Variable"/> set (Ferry sets it only for the ssh it starts), this run is that
    /// askpass: the arguments become "askpass &lt;prompt&gt;".
    /// </summary>
    public static string[] AsAskpass(string[] args, string? variable) =>
        variable is { Length: > 0 } ? ["askpass", .. args.SkipWhile(a => a == "askpass").Take(1)] : args;

    /// <summary>A usage error: say what's wrong and return <see cref="ExitCodes.Usage"/>.</summary>
    public static int Usage(AgentConsole console, string message)
    {
        console.Error.WriteLine(message);
        return ExitCodes.Usage;
    }
}
