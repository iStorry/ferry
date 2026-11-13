using System.ComponentModel;
using System.Diagnostics;

namespace Ferry.Core.Tools;

/// <summary>
/// Runs a program the user installs (ffmpeg, yt-dlp, 7-Zip) and turns what becomes of it into something for the user:
/// it isn't there, it stopped with an error and said why, or it was cancelled and taken down with everything it started.
/// </summary>
public static class ToolProcess
{
    // the last lines a tool wrote to standard error are what say why it stopped; the rest is progress and warnings
    private const int MostErrorLines = 3;

    /// <summary>What a tool that can't be started means for the user.</summary>
    public static string NotInstalled(string tool) => $"{Path.GetFileName(tool)} isn't installed or isn't on PATH.";

    /// <summary>Run <paramref name="tool"/> with <paramref name="arguments"/> to its end, each argument passed as it is.</summary>
    /// <param name="onStdoutLine">told each line the tool writes to standard output, as it comes; its output is read either way</param>
    /// <param name="ifMissing">what the user can do when the tool isn't there, said after <see cref="NotInstalled"/></param>
    /// <exception cref="StageException">the tool isn't there, or it stopped with an error: its last words on standard error say why</exception>
    /// <exception cref="OperationCanceledException">cancelled; the tool and whatever it started are gone</exception>
    public static async Task RunAsync(string tool, IEnumerable<string> arguments, Action<string>? onStdoutLine, Stage stage,
                                      CancellationToken cancel, string? ifMissing = null)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var errors = new Queue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
                onStdoutLine?.Invoke(line);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line)
                return;
            lock (errors)
            {
                errors.Enqueue(line);
                if (errors.Count > MostErrorLines)
                    errors.Dequeue();
            }
        };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new StageException(stage, ifMissing is null ? NotInstalled(tool) : $"{NotInstalled(tool)} {ifMissing}", inner: e);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None); // its last output is read before it is let go
            throw;
        }
        if (process.ExitCode == 0)
            return;
        string said;
        lock (errors)
            said = string.Join(' ', errors);
        throw new StageException(stage, $"{Path.GetFileName(tool)} stopped with code {process.ExitCode}{(said.Length > 0 ? $": {said}" : ".")}");
    }
}
