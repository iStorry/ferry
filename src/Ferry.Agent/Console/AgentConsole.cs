namespace Ferry.Agent.Console;

/// <summary>
/// Where the agent's commands read and write: the process's console, or strings in tests. Results go to
/// <see cref="Out"/>, messages to <see cref="Error"/>.
/// </summary>
internal sealed class AgentConsole(TextWriter output, TextWriter error, Func<Stream>? openInput = null, Func<Stream>? openOutput = null)
{
    public TextWriter Out { get; } = output;
    public TextWriter Error { get; } = error;

    /// <summary>The raw stdin bytes, for the agent's attach channel.</summary>
    public Stream OpenInput() => (openInput ?? System.Console.OpenStandardInput)();

    /// <summary>The raw stdout bytes, for the agent's attach channel.</summary>
    public Stream OpenOutput() => (openOutput ?? System.Console.OpenStandardOutput)();

    public static AgentConsole ForProcess() => new(System.Console.Out, System.Console.Error);
}
