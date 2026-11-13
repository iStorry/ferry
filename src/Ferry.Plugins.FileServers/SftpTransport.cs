using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Ferry.Core;
using Ferry.Core.Sites;

namespace Ferry.Plugins.FileServers;

/// <summary>
/// A file over SFTP, through the OpenSSH client that comes with Windows and Linux. Batch mode means it never stops to ask:
/// no key for the server, or a host not yet in known_hosts, comes back as its error.
/// </summary>
internal sealed class SftpTransport(Uri link, string path) : IFileTransport
{
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(500);

    public async Task TransferAsync(TransportJob job, CancellationToken cancel)
    {
        if (path.Contains('"', StringComparison.Ordinal) || job.PartPath.Contains('"', StringComparison.Ordinal))
            throw new StageException(Stage.Download, "SFTP can't fetch a path with a double quote in it.");
        var start = new ProcessStartInfo("sftp") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-q", "-b", "-", "-o", "BatchMode=yes" })
            start.ArgumentList.Add(argument);
        if (link.Port > 0)
        {
            start.ArgumentList.Add("-P");
            start.ArgumentList.Add(link.Port.ToString(CultureInfo.InvariantCulture));
        }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(link.UserInfo.Length > 0 ? $"{Uri.UnescapeDataString(link.UserInfo)}@{link.Host}" : link.Host);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new StageException(Stage.Download, "Ferry couldn't start OpenSSH's sftp. Install the OpenSSH client.", inner: e);
        }
        var errors = process.StandardError.ReadToEndAsync(cancel);
        _ = process.StandardOutput.ReadToEndAsync(cancel);
        await process.StandardInput.WriteLineAsync($"reget \"{path}\" \"{job.PartPath}\"");
        process.StandardInput.Close();
        try
        {
            using var ticks = new PeriodicTimer(ProgressEvery);
            var ended = process.WaitForExitAsync(cancel);
            while (!ended.IsCompleted)
            {
                await Task.WhenAny(ended, ticks.WaitForNextTickAsync(cancel).AsTask());
                if (File.Exists(job.PartPath))
                    job.OnBytes(new FileInfo(job.PartPath).Length);
            }
            await ended;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0)
            throw new StageException(Stage.Download, $"sftp couldn't fetch the file: {(await errors).Trim()}");
    }
}
