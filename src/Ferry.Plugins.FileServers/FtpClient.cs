using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Sites;

namespace Ferry.Plugins.FileServers;

/// <param name="Secure">explicit TLS on both channels (ftps://)</param>
internal sealed record FtpLogin(string Host, int Port, string User, string Password, bool Secure);

/// <summary>What Ferry asks of an FTP server, in passive mode: log in, how big, and from where to send a file.</summary>
internal sealed partial class FtpClient : IAsyncDisposable
{
    private const int BufferBytes = 1 << 16;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);

    private readonly TcpClient _socket;
    private readonly FtpLogin _login;
    private readonly Stage _stage;
    private Stream _stream = Stream.Null;
    private StreamReader _reader = StreamReader.Null;

    private FtpClient(TcpClient socket, FtpLogin login, Stage stage) => (_socket, _login, _stage) = (socket, login, stage);

    /// <exception cref="StageException">the server couldn't be reached, or refused the login</exception>
    public static async Task<FtpClient> ConnectAsync(FtpLogin login, Stage stage, CancellationToken cancel)
    {
        var socket = new TcpClient();
        var client = new FtpClient(socket, login, stage);
        try
        {
            await socket.ConnectAsync(login.Host, login.Port, cancel);
            client._stream = socket.GetStream();
            client._reader = new StreamReader(client._stream, Encoding.UTF8, leaveOpen: true);
            await client.ExpectAsync(2, cancel);
            if (login.Secure)
            {
                await client.CommandAsync("AUTH TLS", 2, cancel);
                var tls = new SslStream(client._stream);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = login.Host }, cancel);
                client._stream = tls;
                client._reader = new StreamReader(tls, Encoding.UTF8, leaveOpen: true);
                await client.CommandAsync("PBSZ 0", 2, cancel);
                await client.CommandAsync("PROT P", 2, cancel);
            }
            var answer = await client.SendAsync($"USER {login.User}", cancel);
            if (answer.Code / 100 == 3)
                answer = await client.SendAsync($"PASS {login.Password}", cancel);
            if (answer.Code / 100 != 2)
                throw new StageException(stage, $"{login.Host} refused the login for {login.User}. Give a password as a \"Password\" header under Login.");
            await client.CommandAsync("TYPE I", 2, cancel);
            return client;
        }
        catch (Exception e) when (e is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            await client.DisposeAsync();
            throw new StageException(stage, $"Could not reach {login.Host}: {e.Message}", isRetryable: true, inner: e);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>The file's size, or null when the server won't say.</summary>
    public async Task<long?> SizeAsync(string path, CancellationToken cancel)
    {
        var answer = await SendAsync($"SIZE {path}", cancel);
        return answer.Code == 213 && long.TryParse(answer.Text, CultureInfo.InvariantCulture, out var size) ? size : null;
    }

    /// <summary>Write the file to <paramref name="target"/> from <paramref name="from"/> on, which is where its stream has to be.</summary>
    /// <exception cref="StageException">the server stopped before the size it gave was reached; retryable, to carry on from there</exception>
    public async Task RetrieveAsync(string path, long from, Stream target, Action<long> onBytes, Func<int, CancellationToken, ValueTask> pace, CancellationToken cancel)
    {
        var size = await SizeAsync(path, cancel);
        var (host, port) = await PassiveAsync(cancel);
        using var data = new TcpClient();
        long done;
        try
        {
            await data.ConnectAsync(host, port, cancel);
            if (from > 0)
                await CommandAsync(FormattableString.Invariant($"REST {from}"), 3, cancel);
            var opened = await SendAsync($"RETR {path}", cancel);
            if (opened.Code / 100 != 1)
                throw new StageException(_stage, $"{_login.Host} wouldn't send {path}: {opened.Text}");
            Stream channel = data.GetStream();
            if (_login.Secure)
            {
                var tls = new SslStream(channel);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _login.Host }, cancel);
                channel = tls;
            }
            await using (channel)
                done = await CopyAsync(channel, target, from, onBytes, pace, cancel);
            await ExpectAsync(2, cancel);
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            throw new StageException(_stage, $"The connection to {_login.Host} dropped: {e.Message}", isRetryable: true, inner: e);
        }
        if (size is { } expected && done < expected)
            throw new StageException(_stage, $"{_login.Host} stopped with {expected - done} bytes of {path} still to come.", isRetryable: true);
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync();
        _socket.Dispose();
    }

    /// <returns>how far into the file the copy got</returns>
    private async Task<long> CopyAsync(Stream channel, Stream target, long from, Action<long> onBytes, Func<int, CancellationToken, ValueTask> pace, CancellationToken cancel)
    {
        var (buffer, done) = (new byte[BufferBytes], from);
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            idle.CancelAfter(IdleLimit);
            int read;
            try
            {
                read = await channel.ReadAsync(buffer, idle.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new StageException(_stage, $"{_login.Host} stopped sending.", isRetryable: true);
            }
            if (read == 0)
                return done;
            await target.WriteAsync(buffer.AsMemory(0, read), cancel);
            done += read;
            onBytes(done);
            await pace(read, cancel);
        }
    }

    /// <summary>Where the server listens for the data channel: extended passive, else plain passive.</summary>
    private async Task<(string Host, int Port)> PassiveAsync(CancellationToken cancel)
    {
        var extended = await SendAsync("EPSV", cancel);
        if (extended.Code == 229 && ExtendedPort().Match(extended.Text) is { Success: true } port)
            return (_login.Host, int.Parse(port.Groups["port"].Value, CultureInfo.InvariantCulture));
        var plain = await SendAsync("PASV", cancel);
        if (plain.Code == 227 && PlainPort().Match(plain.Text) is { Success: true } match)
            // the server's own address in its answer can be anything, even another machine: only its port counts
            return (_login.Host, (int.Parse(match.Groups["high"].Value, CultureInfo.InvariantCulture) * 256) + int.Parse(match.Groups["low"].Value, CultureInfo.InvariantCulture));
        throw new StageException(_stage, $"{_login.Host} won't open a data connection: {plain.Text}");
    }

    private async Task CommandAsync(string command, int expected, CancellationToken cancel)
    {
        var answer = await SendAsync(command, cancel);
        if (answer.Code / 100 != expected)
            throw new StageException(_stage, $"{_login.Host} answered {answer.Code}: {answer.Text}");
    }

    /// <summary>One command line; nothing that would end the line early, and so be a command of its own, goes in one.</summary>
    private async Task<(int Code, string Text)> SendAsync(string command, CancellationToken cancel)
    {
        if (command.Any(char.IsControl))
            throw new StageException(_stage, "The login or the path has a line break or another control character in it, which can't be sent to an FTP server.");
        await _stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"), cancel);
        await _stream.FlushAsync(cancel);
        return await ReadAnswerAsync(cancel);
    }

    private async Task ExpectAsync(int expected, CancellationToken cancel)
    {
        var answer = await ReadAnswerAsync(cancel);
        if (answer.Code / 100 != expected)
            throw new StageException(_stage, $"{_login.Host} answered {answer.Code}: {answer.Text}");
    }

    /// <summary>One answer: "123 text", or "123-text" and lines up to "123 text".</summary>
    private async Task<(int Code, string Text)> ReadAnswerAsync(CancellationToken cancel)
    {
        var line = await _reader.ReadLineAsync(cancel) ?? throw new IOException("The server closed the connection.");
        if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), CultureInfo.InvariantCulture, out var code))
            throw new StageException(_stage, $"{_login.Host} doesn't answer like an FTP server.");
        if (line.Length > 3 && line[3] == '-')
        {
            var ending = $"{line[..3]} ";
            do
                line = await _reader.ReadLineAsync(cancel) ?? throw new IOException("The server closed the connection.");
            while (!line.StartsWith(ending, StringComparison.Ordinal));
        }
        return (code, line.Length > 4 ? line[4..].Trim() : "");
    }

    [GeneratedRegex(@"\(\|\|\|(?<port>\d+)\|\)")]
    private static partial Regex ExtendedPort();

    [GeneratedRegex(@"\d+,\d+,\d+,\d+,(?<high>\d+),(?<low>\d+)")]
    private static partial Regex PlainPort();
}

/// <summary>A file over FTP, carrying on from what is in the part.</summary>
internal sealed class FtpTransport(FtpLogin login, string path) : IFileTransport
{
    public async Task TransferAsync(TransportJob job, CancellationToken cancel)
    {
        await using var part = new FileStream(job.PartPath, new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Options = FileOptions.Asynchronous });
        await using var ftp = await FtpClient.ConnectAsync(login, Stage.Download, cancel);
        await ftp.RetrieveAsync(path, part.Length, part, job.OnBytes, job.Pace, cancel);
    }
}
