using System.CommandLine;
using Ferry.Agent.Console;
using Ferry.Plugins.Server;

namespace Ferry.Agent;

/// <summary>
/// `ferry askpass`, hidden: what ssh runs (SSH_ASKPASS) for a password or passphrase prompt. It asks the Ferry that
/// started ssh, over a one-time pipe (<see cref="Askpass"/>), and prints the answer; with nothing to answer it fails,
/// so ssh gives up instead of trying an empty password.
/// </summary>
internal static class AskpassCommand
{
    public static Command Create(AgentConsole console)
    {
        var prompt = new Argument<string?>("prompt") { Arity = ArgumentArity.ZeroOrOne, Description = "What ssh asks" };
        var command = new Command("askpass", "Answer ssh's password prompt for the Ferry that started it.") { Hidden = true, Arguments = { prompt } };
        command.SetAction(async (result, cancel) =>
        {
            var (answer, problem) = await Askpass.AnswerAsync(Environment.GetEnvironmentVariable(Askpass.Variable), result.GetValue(prompt) ?? "", cancel);
            if (string.IsNullOrEmpty(answer))
            {
                // ssh shows its askpass's stderr: this is how a refused login says why
                await console.Error.WriteLineAsync($"ferry askpass: {problem}");
                return ExitCodes.Failed;
            }
            await console.Out.WriteAsync(answer + "\n");
            await console.Out.FlushAsync(cancel);
            return ExitCodes.Done;
        });
        return command;
    }
}
