using Ferry.Agent;
using Ferry.Agent.Console;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
// ssh runs "ferry askpass" without a console, and its stdout is a pipe
return await Commands.RunAsync(Commands.AsAskpass(args, Environment.GetEnvironmentVariable(Ferry.Plugins.Server.Askpass.Variable)),
                               AgentConsole.ForProcess(), cancel.Token);
