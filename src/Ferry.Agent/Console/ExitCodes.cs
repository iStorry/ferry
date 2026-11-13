namespace Ferry.Agent.Console;

/// <summary>What the process's exit code means; the PC relies on these.</summary>
internal static class ExitCodes
{
    public const int Done = 0;

    public const int Failed = 1;

    /// <summary>Wrong arguments.</summary>
    public const int Usage = 2;

    /// <summary>Stopped by a signal.</summary>
    public const int Interrupted = 130;
}
