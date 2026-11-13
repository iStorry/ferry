using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Ferry.Plugins.Server;

/// <summary>
/// How the agent and its server are doing, as the agent sees it (the <see cref="AgentCommand.Stats"/> answer). The
/// server's figures come from /proc and are null where it has none. CPU is time used so far: the PC works out a
/// percentage from two of these.
/// </summary>
/// <param name="AgentMemoryBytes">the agent's resident memory</param>
/// <param name="AgentCpuSeconds">CPU time the agent has used since it started</param>
/// <param name="TakenAt">when this was measured, by the server's clock</param>
/// <param name="Sessions">PCs connected to the agent now</param>
/// <param name="DownloadBytesPerSecond">from the sites to the server, all downloads together</param>
public sealed record AgentStats(
    long AgentMemoryBytes,
    double AgentCpuSeconds,
    double AgentUptimeSeconds,
    int AgentThreads,
    DateTimeOffset TakenAt,
    string Host,
    string System,
    int Cpus,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    double? Load1,
    double? Load5,
    double? Load15,
    long? DiskTotalBytes,
    long? DiskFreeBytes,
    int Sessions,
    int Downloading,
    int Waiting,
    int Finished,
    double DownloadBytesPerSecond)
{
    /// <summary>The most of the log one answer carries: enough to read, small enough to send every few seconds.</summary>
    public const int LogTailBytes = 64 << 10;

    /// <summary>Measure now.</summary>
    /// <param name="folder">the download folder, for the disk figures</param>
    public static AgentStats Collect(string folder, int sessions, int downloading, int waiting, int finished, double bytesPerSecond)
    {
        using var me = Process.GetCurrentProcess();
        var (total, available) = Memory(Read("/proc/meminfo"));
        var load = Load(Read("/proc/loadavg"));
        var (diskTotal, diskFree) = Disk(folder);
        return new AgentStats(me.WorkingSet64, me.TotalProcessorTime.TotalSeconds, (DateTime.Now - me.StartTime).TotalSeconds,
                              me.Threads.Count, DateTimeOffset.UtcNow, Environment.MachineName, RuntimeInformation.OSDescription,
                              Environment.ProcessorCount, total, available, load?.One, load?.Five, load?.Fifteen, diskTotal, diskFree,
                              sessions, downloading, waiting, finished, bytesPerSecond);
    }

    /// <summary>The end of <paramref name="path"/>, at most <paramref name="maxBytes"/>, from a whole line; "" when there is none.</summary>
    public static async Task<string> TailAsync(string path, int maxBytes)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, file.Length - maxBytes);
            file.Position = start;
            var bytes = new byte[file.Length - start];
            await file.ReadExactlyAsync(bytes);
            var text = Encoding.UTF8.GetString(bytes);
            // cut in the middle of a line: start at the next one
            return start > 0 && text.IndexOf('\n', StringComparison.Ordinal) is var newline and >= 0 ? text[(newline + 1)..] : text;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return "";
        }
    }

    /// <summary>MemTotal and MemAvailable from /proc/meminfo, in bytes.</summary>
    internal static (long? Total, long? Available) Memory(string? meminfo)
    {
        long? Field(string name)
        {
            foreach (var line in (meminfo ?? "").Split('\n'))
            {
                if (!line.StartsWith(name + ":", StringComparison.Ordinal))
                    continue;
                var number = line[(name.Length + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var kib) ? kib * 1024 : null;
            }
            return null;
        }
        return (Field("MemTotal"), Field("MemAvailable"));
    }

    /// <summary>The 1, 5 and 15 minute load averages from /proc/loadavg.</summary>
    internal static (double One, double Five, double Fifteen)? Load(string? loadavg)
    {
        var parts = (loadavg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double Parse(int i) => double.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture);
        return parts.Length >= 3 && parts.Take(3).All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            ? (Parse(0), Parse(1), Parse(2))
            : null;
    }

    private static (long? Total, long? Free) Disk(string folder)
    {
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(folder));
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // not Linux, or not readable: those figures stay unknown
        }
    }
}
