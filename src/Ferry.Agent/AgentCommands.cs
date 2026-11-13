using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Ferry.Agent.Console;
using Ferry.Plugins.Server;

namespace Ferry.Agent;

/// <summary>
/// `ferry agent`, hidden: the server side of server mode (§4.5). `run` is the agent itself, in the foreground;
/// `start` runs it detached; `attach` is what the PC runs over SSH, joining stdin/stdout to the agent's socket and
/// starting the agent first if needed; `stop` asks it to stop.
/// </summary>
internal static partial class AgentCommands
{
    private const int SigTerm = 15;
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(10);
    private static readonly string[] SetsidPaths = ["/usr/bin/setsid", "/bin/setsid"];

    public static Command Create(AgentConsole console)
    {
        var root = new Option<string?>("--root") { Description = "The agent's folder (default ~/.ferry)", Hidden = true };

        var run = new Command("run", "Run the agent in the foreground until stopped.") { root };
        run.SetAction((result, cancel) => RunAsync(Paths(result, root), cancel));

        var start = new Command("start", "Start the agent in the background, unless it is running.") { root };
        start.SetAction(async (result, cancel) =>
        {
            var paths = Paths(result, root);
            if (await ConnectAsync(paths, cancel) is { } running)
            {
                running.Dispose();
                return ExitCodes.Done;
            }
            return await StartAsync(paths, cancel) ? ExitCodes.Done : Unreachable(console, paths);
        });

        var attach = new Command("attach", "Join stdin/stdout to the agent, starting it if needed.") { root };
        attach.SetAction(async (result, cancel) =>
        {
            var paths = Paths(result, root);
            var socket = await ConnectAsync(paths, cancel);
            if (socket is null && await StartAsync(paths, cancel))
                socket = await ConnectAsync(paths, cancel);
            if (socket is null)
                return Unreachable(console, paths);
            await PumpAsync(socket, console.OpenInput(), console.OpenOutput(), cancel);
            return ExitCodes.Done;
        });

        var stop = new Command("stop", "Stop the agent; downloads carry on when it starts again.") { root };
        stop.SetAction(async (result, cancel) => await StopAsync(Paths(result, root), cancel));

        var item = new Argument<string>("item") { Description = "The finished file's id in the agent's queue" };
        var offset = new Argument<long>("offset") { Description = "The first byte" };
        var length = new Argument<long>("length") { Description = "How many bytes" };
        var read = new Command("read", "Print part of a finished file, for the PC to bring over.") { root, item, offset, length };
        read.SetAction(async (result, cancel) =>
        {
            if (!Guid.TryParse(result.GetValue(item), out var id))
                return Commands.Usage(console, $"\"{result.GetValue(item)}\" is not a file's id.");
            try
            {
                var file = AgentRead.FinishedFile(Paths(result, root), id);
                await using var output = console.OpenOutput();
                await AgentRead.CopyAsync(file, result.GetValue(offset), result.GetValue(length), output, cancel);
                return ExitCodes.Done;
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // the PC shows this; a PC that stopped reading (another lane took the rest) is gone and doesn't care
                console.Error.WriteLine(e.Message);
                return ExitCodes.Failed;
            }
        });

        return new Command("agent", "The server side of server mode.") { Hidden = true, Subcommands = { run, start, attach, stop, read } };
    }

    /// <summary>The agent in the foreground: until cancelled, or SIGTERM on Linux and macOS.</summary>
    public static async Task<int> RunAsync(AgentPaths paths, CancellationToken cancel)
    {
        using var only = AgentHost.TryLock(paths);
        if (only is null)
            return ExitCodes.Done; // another agent runs: attach finds it by its socket
        // what nothing else caught goes to the log (its standard error) before the process ends
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AgentHost.Log($"crashed: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AgentHost.Log($"unobserved: {e.Exception}");
            e.SetObserved();
        };
        AgentHost.Log($"agent {Core.Product.Build} starting (pid {Environment.ProcessId})");
        // at once, so a stop never finds the lock held and an older agent's pid in the file
        await File.WriteAllTextAsync(paths.PidFile, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), cancel);
#if SERVER_AGENT
        var (plugins, browsing, lookup) = ServerPlugins.Load(paths);
        await using var host = AgentHost.Start(paths, plugins: plugins, browsing: browsing, lookup: lookup);
#else
        await using var host = AgentHost.Start(paths);
#endif
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        using var terminate = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                stop.Cancel();
            });
        try
        {
            await host.ListenAsync(stop.Token);
        }
        finally
        {
            File.Delete(paths.PidFile);
            AgentHost.Log("agent stopping");
        }
        return ExitCodes.Done;
    }

    /// <summary>Copy <paramref name="input"/> to the agent and its answers to <paramref name="output"/> until either end closes.</summary>
    public static async Task PumpAsync(Socket socket, Stream input, Stream output, CancellationToken cancel)
    {
        await using var agent = new NetworkStream(socket, ownsSocket: true);
        var up = Task.Run(async () =>
        {
            try
            {
                await input.CopyToAsync(agent, cancel);
                socket.Shutdown(SocketShutdown.Send); // the PC is done: the agent ends the session
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // either end went away
            }
        }, cancel);
        var down = Task.Run(async () =>
        {
            try
            {
                await agent.CopyToAsync(output, cancel);
                await output.FlushAsync(cancel);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // either end went away
            }
        }, cancel);
        await Task.WhenAny(up, down);
        if (!down.IsCompleted)
            await down;
    }

    /// <summary>A connection to the running agent, or null if none is listening.</summary>
    public static async Task<Socket?> ConnectAsync(AgentPaths paths, CancellationToken cancel)
    {
        if (!File.Exists(paths.SocketPath))
            return null;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(paths.SocketPath), cancel);
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    /// <summary>Start `agent run` detached (in its own session, so logging out doesn't stop it) and wait for its socket.</summary>
    private static async Task<bool> StartAsync(AgentPaths paths, CancellationToken cancel)
    {
        if (Environment.ProcessPath is not { } self)
            return false;
        string[] arguments = ["agent", "run", "--root", paths.Root];
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            info = new ProcessStartInfo(self, arguments);
        }
        else
        {
            // its output goes to agent.log, never to pipes: this process ends, and a pipe nobody reads fills up and
            // blocks the agent the next time it writes. The log is started afresh when it has grown past 1 MB.
            Directory.CreateDirectory(paths.Root);
            if (File.Exists(paths.LogFile) && new FileInfo(paths.LogFile).Length > 1 << 20)
                File.Move(paths.LogFile, paths.LogFile + ".old", overwrite: true);
            string[] shell = ["/bin/sh", "-c", "exec \"$0\" agent run --root \"$1\" </dev/null >>\"$2\" 2>&1", self, paths.Root, paths.LogFile];
            var setsid = SetsidPaths.FirstOrDefault(File.Exists);
            info = setsid is null ? new ProcessStartInfo(shell[0], shell[1..]) : new ProcessStartInfo(setsid, ["-f", .. shell]);
        }
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        using (var process = Process.Start(info))
        {
            if (process is null)
                return false;
            process.StandardInput.Close();
        }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartWait)
        {
            if (await ConnectAsync(paths, cancel) is { } socket)
            {
                socket.Dispose();
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancel);
        }
        return false;
    }

    private static async Task<int> StopAsync(AgentPaths paths, CancellationToken cancel)
    {
        var pid = File.Exists(paths.PidFile)
                  && int.TryParse(await File.ReadAllTextAsync(paths.PidFile, cancel), NumberStyles.Integer, CultureInfo.InvariantCulture, out var read)
            ? read
            : (int?)null;
        bool locked;
        using (var free = AgentHost.TryLock(paths))
            locked = free is null;
        // Nothing holds the lock, so no agent of this build runs. The pid is then either one left by an agent that was
        // killed (and may be another program's by now), or an agent from before the lock: only that one is stopped.
        if (!locked && !(pid is { } older && IsAgentProcess(older)))
        {
            File.Delete(paths.PidFile);
            File.Delete(paths.SocketPath);
            return ExitCodes.Done;
        }
        if (pid is null || !IsAgentProcess(pid.Value))
            return ExitCodes.Done; // locked, but the pid isn't an agent's (yet): never signal another program
        Process agent;
        try
        {
            agent = Process.GetProcessById(pid.Value);
        }
        catch (ArgumentException)
        {
            File.Delete(paths.PidFile); // it went without cleaning up
            return ExitCodes.Done;
        }
        using (agent)
        {
            if (OperatingSystem.IsWindows())
                agent.Kill();
            else if (Kill(pid.Value, SigTerm) != 0)
                return ExitCodes.Failed;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            wait.CancelAfter(StartWait);
            try
            {
                await agent.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                agent.Kill(); // it didn't stop by itself in time; what it downloaded is on disk and its state saved often
                await agent.WaitForExitAsync(cancel);
            }
        }
        // after SIGTERM the agent removes these itself; a killed one (Windows) can't
        File.Delete(paths.PidFile);
        File.Delete(paths.SocketPath);
        return ExitCodes.Done;
    }

    /// <summary>Whether process <paramref name="pid"/> is "ferry agent run" (Linux: its command line); elsewhere, whether it runs.</summary>
    private static bool IsAgentProcess(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var arguments = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0');
                return arguments.Contains("agent") && arguments.Contains("run");
            }
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return false; // gone, or not ours
        }
    }

    private static AgentPaths Paths(ParseResult result, Option<string?> root) =>
        result.GetValue(root) is { } folder ? new AgentPaths(Path.GetFullPath(folder)) : AgentPaths.Default;

    private static int Unreachable(AgentConsole console, AgentPaths paths)
    {
        console.Error.WriteLine($"The agent did not start (no answer on {paths.SocketPath}).");
        return ExitCodes.Failed;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
