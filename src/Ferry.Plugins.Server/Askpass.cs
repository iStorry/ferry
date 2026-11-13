using Ferry.Core;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ferry.Plugins.Server;

/// <summary>
/// Hands one secret (setup's password, or a key's passphrase) to ssh's prompt, without a terminal and without the
/// secret ever being in an environment variable or on a command line (docs/openssh-plan.md §4.4). ssh runs
/// <c>ferry askpass "&lt;prompt&gt;"</c> (SSH_ASKPASS, with SSH_ASKPASS_REQUIRE=force); that asks this one-time pipe
/// (only this user can open it, and it wants the random token from <see cref="Variable"/>) and prints the answer.
/// A prompt that isn't for a password or passphrase (a one-time code, a new password) gets an empty answer.
/// </summary>
public sealed partial class Askpass : IAsyncDisposable
{
    /// <summary>"&lt;pipe name&gt;;&lt;token&gt;", set for ssh and passed on to askpass.</summary>
    public const string Variable = "FERRY_ASKPASS";

    private const string CliAssembly = "ferry";
    private const int TokenBytes = 32;
    private static readonly TimeSpan ClientWait = TimeSpan.FromSeconds(10);
    private readonly NamedPipeServerStream _pipe;
    private readonly string _name;
    private readonly string _token;
    private readonly string _secret;
    private readonly string _program;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;

    private Askpass(string secret, string program)
    {
        (_secret, _program) = (secret, program);
        _name = $"ferry-askpass-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))}";
        _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));
        _pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
                                          PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _serving = ServeAsync();
    }

    /// <summary>Be ready to answer one prompt with <paramref name="secret"/>; pass <see cref="Environment"/> to ssh.</summary>
    /// <exception cref="StageException">there is no ferry program to act as askpass (<see cref="Stage.Server"/>)</exception>
    public static Askpass Start(string secret) => new(secret, Program());

    /// <summary>With <paramref name="program"/> as askpass; for tests.</summary>
    internal static Askpass Start(string secret, string program) => new(secret, program);

    /// <summary>The variables ssh needs to use askpass, for <see cref="SshProcess.Start"/>.</summary>
    public IReadOnlyDictionary<string, string?> Environment => new Dictionary<string, string?>
    {
        ["SSH_ASKPASS"] = _program,
        ["SSH_ASKPASS_REQUIRE"] = "force",
        [Variable] = $"{_name};{_token}",
    };

    /// <summary>The answer to <paramref name="prompt"/>, from the pipe named in <paramref name="variable"/>; or, when
    /// there is none to give, why (no variable, no pipe, a wrong token, a prompt it doesn't answer). This is
    /// <c>ferry askpass</c>, which prints the problem on stderr, where ssh passes it on.</summary>
    public static async Task<(string? Answer, string? Problem)> AnswerAsync(string? variable, string prompt, CancellationToken cancel)
    {
        if (variable?.Split(';') is not [var name, var token] || name.Length == 0)
            return (null, $"{Variable} isn't set: this is run by ssh for the Ferry that started it.");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        wait.CancelAfter(ClientWait);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(wait.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
            await writer.WriteLineAsync(token.AsMemory(), wait.Token);
            await writer.WriteLineAsync(OneLine(prompt).AsMemory(), wait.Token);
            await writer.FlushAsync(wait.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            var answer = await reader.ReadLineAsync(wait.Token);
            return string.IsNullOrEmpty(answer)
                ? (null, $"Ferry gave no answer to \"{OneLine(prompt).Trim()}\" (it answers only its password or passphrase prompt, once).")
                : (answer, null);
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return (null, $"Ferry's answer couldn't be had ({e.GetType().Name}: {e.Message}).");
        }
    }

    /// <summary>Whether <paramref name="prompt"/> asks for the password or passphrase Ferry holds.</summary>
    internal static bool AsksForSecret(string prompt) =>
        (prompt.Contains("password", StringComparison.OrdinalIgnoreCase) || prompt.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
        && !prompt.Contains("new password", StringComparison.OrdinalIgnoreCase)
        && !prompt.Contains("new passphrase", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
            // nobody asked
        }
        await _pipe.DisposeAsync();
        _stop.Dispose();
    }

    /// <summary>Answer the first caller, then no one: with the secret if it has the token and the prompt asks for it.</summary>
    private async Task ServeAsync()
    {
        await Task.Yield();
        try
        {
            await _pipe.WaitForConnectionAsync(_stop.Token);
            using var reader = new StreamReader(_pipe, new UTF8Encoding(false), leaveOpen: true);
            var token = await reader.ReadLineAsync(_stop.Token) ?? "";
            var prompt = await reader.ReadLineAsync(_stop.Token) ?? "";
            var right = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(_token));
            await using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
            await writer.WriteLineAsync((right && AsksForSecret(prompt) ? _secret : "").AsMemory(), _stop.Token);
            await writer.FlushAsync(_stop.Token);
            if (OperatingSystem.IsWindows())
                _pipe.WaitForPipeDrain(); // read before the pipe closes
        }
        catch (IOException)
        {
            // the caller went away
        }
    }

    /// <summary>The ferry program to run as askpass instead of the one found; for tests, which run under a test host.</summary>
    internal static string? ProgramOverride { get; set; }

    /// <summary>The ferry program to run as askpass: this one when it is the CLI, else the CLI the app carries.</summary>
    private static string Program()
    {
        if (ProgramOverride is { } chosen)
            return chosen;
        // by its assembly, not its file: to Windows the app's Ferry.exe is the CLI's ferry.exe, and the app prints no
        // answer, so ssh would send an empty password
        if (Assembly.GetEntryAssembly()?.GetName().Name == CliAssembly && System.Environment.ProcessPath is { } self)
            return ShortPath(self);
        var exe = OperatingSystem.IsWindows() ? "ferry.exe" : "ferry";
        var carried = Path.Combine(AppContext.BaseDirectory, "cli", exe);
        if (File.Exists(carried))
            return ShortPath(carried);
        throw new StageException(Stage.Server, $"Ferry's command-line program isn't at {carried}; it answers ssh's password prompt.");
    }

    /// <summary>
    /// Windows: the path without spaces (its 8.3 form), since Win32-OpenSSH starts SSH_ASKPASS itself and a space in it
    /// (a user name such as "Jat Singh") is a risk there. Unchanged elsewhere, or where 8.3 names are turned off.
    /// </summary>
    private static unsafe string ShortPath(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains(' ', StringComparison.Ordinal))
            return path;
        var buffer = new char[path.Length + 1];
        fixed (char* start = buffer)
        {
            var length = GetShortPathName(path, start, (uint)buffer.Length);
            return length > 0 && length < buffer.Length ? new string(start, 0, (int)length) : path;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetShortPathName(string longPath, char* shortPath, uint length);

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
