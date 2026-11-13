using Ferry.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Ferry.Plugins.Server;

/// <summary>What a run of ssh that went to its end left: its exit code, stdout as text, and the end of its stderr.</summary>
public sealed record SshResult(int ExitCode, string Output, string Errors);

/// <summary>
/// One run of ssh (or ssh-keygen): its stdin and stdout as raw byte streams, the last 8 KB of its stderr kept for the
/// error message. On Windows it is in a job that kills it when this process ends, however that happens, so no ssh.exe
/// is left behind holding a connection. Disposing closes its stdin (the server sees end of input), waits a few
/// seconds, then kills it and anything it started.
/// </summary>
public sealed class SshProcess : IAsyncDisposable
{
    private const int ErrorTailBytes = 8 << 10;
    private const int CopyBufferBytes = 1 << 20;
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorsWait = TimeSpan.FromSeconds(2);
    private readonly Process _process;
    private readonly ErrorTail _errors = new(ErrorTailBytes);
    private readonly Task _readingErrors;
    private int _disposed;

    private SshProcess(Process process)
    {
        _process = process;
        Exited = process.WaitForExitAsync();
        _readingErrors = _errors.ReadAllAsync(process.StandardError.BaseStream);
    }

    /// <summary>Its stdin; closing it is end of input for the remote command.</summary>
    public Stream Input => _process.StandardInput.BaseStream;

    /// <summary>Its stdout, as bytes.</summary>
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <summary>Completes when it has exited.</summary>
    public Task Exited { get; }

    /// <summary>Its exit code once it has exited: ssh's own failures are 255, anything else is the remote command's.</summary>
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>The last 8 KB of its stderr so far.</summary>
    public string Errors => _errors.Text;

    /// <summary>Wait for the rest of its stderr, for a moment: something it started may hold the stream open after it has gone.</summary>
    public async Task ErrorsEndedAsync(CancellationToken cancel)
    {
        try
        {
            await _readingErrors.WaitAsync(ErrorsWait, cancel);
        }
        catch (TimeoutException)
        {
            // what has come so far will do
        }
    }

    /// <summary>Start <paramref name="program"/> with <paramref name="arguments"/>, every stream redirected.</summary>
    /// <param name="environment">variables to set for it; a null value removes one</param>
    /// <exception cref="StageException">it could not be started (<see cref="Stage.Server"/>)</exception>
    public static SshProcess Start(string program, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var info = new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // an ssh-agent or askpass of the user's is never asked: the login is Ferry's, and askpass is set when wanted
        info.Environment.Remove("SSH_AUTH_SOCK");
        info.Environment.Remove("SSH_ASKPASS");
        info.Environment.Remove("SSH_ASKPASS_REQUIRE");
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
                info.Environment.Remove(name);
            else
                info.Environment[name] = value;
        }
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new StageException(Stage.Server, $"{program} could not be started.");
        }
        catch (Win32Exception e)
        {
            throw new StageException(Stage.Server, $"{program} could not be started: {e.Message}", inner: e);
        }
        if (OperatingSystem.IsWindows())
            KillOnCloseJob.Add(process);
        return new SshProcess(process);
    }

    /// <summary>
    /// Run to the end: <paramref name="input"/> (if any) is copied to its stdin, which is then closed, and its stdout
    /// is read as UTF-8 text. Cancelling kills it.
    /// </summary>
    /// <exception cref="StageException">it could not be started</exception>
    public static async Task<SshResult> RunAsync(string program, IEnumerable<string> arguments, Stream? input, CancellationToken cancel,
                                                 IReadOnlyDictionary<string, string?>? environment = null)
    {
        await using var ssh = Start(program, arguments, environment);
        using var output = new MemoryStream();
        try
        {
            var reading = ssh.Output.CopyToAsync(output, CopyBufferBytes, cancel);
            try
            {
                if (input is not null)
                    await input.CopyToAsync(ssh.Input, CopyBufferBytes, cancel);
            }
            catch (IOException)
            {
                // it stopped reading (it failed, or ended early): its exit code says which
            }
            ssh.CloseInput();
            await reading;
            await ssh.Exited.WaitAsync(cancel);
            await ssh._readingErrors.WaitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            ssh.Kill(); // at once, not after the few seconds disposing gives it
            throw;
        }
        return new SshResult(ssh._process.ExitCode, Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length), ssh.Errors);
    }

    /// <summary>End of input for the remote command.</summary>
    public void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // it has gone already, or whoever wrote to it (the agent's client) closed its stream first
        }
    }

    /// <summary>
    /// Close its stdin, give it a few seconds to end by itself, then kill it and what it started. Returns once
    /// <see cref="Exited"/> has completed, unless even a killed process isn't seen to exit within a few seconds.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CloseInput();
        if (!await EndsInTimeAsync(Exited))
            Kill();
        if (!await EndsInTimeAsync(_readingErrors))
            Kill(); // something it started still holds stderr open: killed with it
        // a killed process closes its pipes before its exit is seen, so stderr's end can come first
        await EndsInTimeAsync(Exited);
        _process.Dispose();
    }

    private static async Task<bool> EndsInTimeAsync(Task task)
    {
        try
        {
            await task.WaitAsync(ExitWait);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Kill it and anything it started, now.</summary>
    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // it has exited already
        }
    }

    /// <summary>The end of a stream, the newest <c>capacity</c> bytes kept.</summary>
    private sealed class ErrorTail(int capacity)
    {
        private readonly Lock _lock = new();
        private readonly byte[] _bytes = new byte[capacity];
        private int _length;

        public string Text
        {
            get
            {
                lock (_lock)
                    return Encoding.UTF8.GetString(_bytes, 0, _length);
            }
        }

        public async Task ReadAllAsync(Stream stream)
        {
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                    Append(buffer.AsSpan(0, read));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // it ended
            }
        }

        private void Append(ReadOnlySpan<byte> chunk)
        {
            lock (_lock)
            {
                if (chunk.Length >= _bytes.Length)
                {
                    chunk[^_bytes.Length..].CopyTo(_bytes);
                    _length = _bytes.Length;
                    return;
                }
                var overflow = _length + chunk.Length - _bytes.Length;
                if (overflow > 0)
                {
                    Buffer.BlockCopy(_bytes, overflow, _bytes, 0, _length - overflow);
                    _length -= overflow;
                }
                chunk.CopyTo(_bytes.AsSpan(_length));
                _length += chunk.Length;
            }
        }
    }
}
