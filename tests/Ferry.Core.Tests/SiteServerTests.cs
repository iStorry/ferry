using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Ferry.Core.Http;
using Ferry.Core.Sites;
using Ferry.Plugins.FileHosts;
using Ferry.Plugins.FileServers;
using Ferry.Plugins.Mega;
using Ferry.Plugins.OpenDirectories;

namespace Ferry.Core.Tests;

/// <summary>How the plugins behave towards servers: stand-ins on loopback for a web server, Gofile, Mega's CDN and an FTP server. Nothing here contacts a real one.</summary>
public sealed class SiteServerTests : IDisposable
{
    private const string UserAgent = "test";
    private const string Listing = "<html><head><title>Index of /files/</title></head><body><h1>Index of /files/</h1><a href=\"a.zip\">a.zip</a> <a href=\"../\">up</a></body></html>";

    private readonly HttpFetcher _http = new();

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task OnlyAsksForTheStartOfWhatMayBeAListing()
    {
        using var web = new FakeWeb((_, response) => FakeWeb.BytesAsync(response, "application/octet-stream", new byte[64 << 10]));
        Assert.Null(await new DirectoryLister(_http, UserAgent).TryListAsync(new Uri(web.Url, "/data/"), RequestDetails.None, CancellationToken.None));
        var request = Assert.Single(web.Requests);
        Assert.Equal(("/data/", $"bytes=0-{DirectoryLister.HeadBytes - 1}"), (request.Path, request.Headers["Range"]));
    }

    [Fact]
    public async Task ReadsTheWholeListingOnceItsStartSaysItIsOne()
    {
        var page = Encoding.UTF8.GetBytes(Listing.Replace("<body>", $"<body><!--{new string('x', 2 * DirectoryLister.HeadBytes)}-->", StringComparison.Ordinal));
        using var web = new FakeWeb((request, response) =>
        {
            if (request.Headers["Range"] is null)
                return FakeWeb.BytesAsync(response, "text/html", page);
            response.StatusCode = (int)HttpStatusCode.PartialContent;
            response.Headers["Content-Range"] = $"bytes 0-{DirectoryLister.HeadBytes - 1}/{page.Length}";
            return FakeWeb.BytesAsync(response, "text/html", page[..DirectoryLister.HeadBytes]);
        });
        var list = await new DirectoryLister(_http, UserAgent).TryListAsync(new Uri(web.Url, "/files/"), RequestDetails.None, CancellationToken.None);
        Assert.Equal([new Uri(web.Url, "/files/a.zip").ToString()], list?.Links);
        Assert.Equal([$"bytes=0-{DirectoryLister.HeadBytes - 1}", null], web.Requests.Select(request => request.Headers["Range"]));
    }

    [Fact]
    public async Task AListingSentWholeIsReadOnce()
    {
        using var web = new FakeWeb((_, response) => FakeWeb.TextAsync(response, "text/html", Listing));
        var list = await new DirectoryLister(_http, UserAgent).TryListAsync(new Uri(web.Url, "/files/"), RequestDetails.None, CancellationToken.None);
        Assert.Equal("files", list?.Title);
        Assert.Equal([new Uri(web.Url, "/files/a.zip").ToString()], list?.Links);
        Assert.Single(web.Requests);
    }

    [Fact]
    public async Task AFolderLinkTheServerRefusesIsAProblemNotAPlainLink()
    {
        using var web = new FakeWeb((_, response) => FakeWeb.StatusAsync(response, HttpStatusCode.NotFound));
        var e = await Assert.ThrowsAsync<StageException>(() => new DirectoryLister(_http, UserAgent).TryListAsync(new Uri(web.Url, "/gone/"), RequestDetails.None, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, e.Status);
    }

    [Theory]
    [InlineData("me\r\nDELE x", "secret")]
    [InlineData("me", "secret\r\nDELE x")]
    public async Task NothingThatEndsAnFtpLineEarlyIsSent(string user, string password)
    {
        using var ftp = new FakeFtp([1, 2, 3], saidSize: 3);
        var e = await Assert.ThrowsAsync<StageException>(() => FtpClient.ConnectAsync(new FtpLogin("127.0.0.1", ftp.Port, user, password, Secure: false), Stage.Resolve, CancellationToken.None));
        Assert.False(e.IsRetryable);
        await ftp.ClientGoneAsync();
        Assert.DoesNotContain(ftp.Commands, command => command.Contains("DELE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnFtpFileThatStopsShortIsNotComplete()
    {
        using var ftp = new FakeFtp([1, 2, 3, 4, 5], saidSize: 10);
        await using var client = await FtpClient.ConnectAsync(new FtpLogin("127.0.0.1", ftp.Port, "me", "secret", Secure: false), Stage.Download, CancellationToken.None);
        using var target = new MemoryStream();
        var e = await Assert.ThrowsAsync<StageException>(() => client.RetrieveAsync("/a.bin", 0, target, _ => { }, (_, _) => ValueTask.CompletedTask, CancellationToken.None));
        Assert.True(e.IsRetryable);
        Assert.Contains("5 bytes", e.Message, StringComparison.Ordinal);
        Assert.Equal(5, target.Length);
    }

    [Fact]
    public async Task AnFtpFileArrivesWhole()
    {
        using var ftp = new FakeFtp([1, 2, 3, 4, 5], saidSize: 5);
        await using var client = await FtpClient.ConnectAsync(new FtpLogin("127.0.0.1", ftp.Port, "me", "secret", Secure: false), Stage.Download, CancellationToken.None);
        using var target = new MemoryStream();
        target.Write([1, 2]);
        await client.RetrieveAsync("/a.bin", 2, target, _ => { }, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal([1, 2, 3, 4, 5], target.ToArray());
        Assert.Contains("REST 2", ftp.Commands);
    }

    [Fact]
    public async Task GofileAsksForAnAccountAgainAfterAFailure()
    {
        var asked = 0;
        using var web = new FakeWeb((_, response) => Interlocked.Increment(ref asked) == 1
            ? FakeWeb.StatusAsync(response, HttpStatusCode.InternalServerError)
            : FakeWeb.TextAsync(response, "application/json", """{"status":"ok","data":{"token":"t1"}}"""));
        using var api = new GofileApi(_http, UserAgent, web.Url, web.Url);
        await Assert.ThrowsAsync<StageException>(() => api.AccountAsync(CancellationToken.None));
        Assert.Equal("t1", await api.AccountAsync(CancellationToken.None));
        Assert.Equal("t1", await api.AccountAsync(CancellationToken.None));
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task GofileReadsAFolderOnceForAllItsFiles()
    {
        var folderReads = 0;
        using var web = new FakeWeb((request, response) => request.Url!.AbsolutePath switch
        {
            "/accounts" => FakeWeb.TextAsync(response, "application/json", """{"status":"ok","data":{"token":"t1"}}"""),
            "/dist/js/config.js" => FakeWeb.TextAsync(response, "application/javascript", """var config = { wt: "w1" };"""),
            "/contents/f1" when Interlocked.Increment(ref folderReads) > 0 => FakeWeb.TextAsync(response, "application/json",
                """{"status":"ok","data":{"name":"Set","children":{"a":{"type":"file","name":"a.zip","link":"https://store.example/a.zip"},"b":{"type":"file","name":"b.zip","link":"https://store.example/b.zip"}}}}"""),
            _ => FakeWeb.StatusAsync(response, HttpStatusCode.NotFound),
        });
        using var api = new GofileApi(_http, UserAgent, web.Url, web.Url);
        Assert.Equal("a.zip", (await api.FindFileAsync("f1", "a", CancellationToken.None))?.Name);
        Assert.Equal("b.zip", (await api.FindFileAsync("f1", "b", CancellationToken.None))?.Name);
        Assert.Equal(1, folderReads);
        Assert.Null(await api.FindFileAsync("f1", "missing", CancellationToken.None));
        Assert.Equal(2, folderReads);
        var folder = web.Requests.First(request => request.Path.StartsWith("/contents/f1", StringComparison.Ordinal));
        Assert.Equal(("Bearer t1", "w1"), (folder.Headers["Authorization"], folder.Headers["X-Website-Token"]));
        Assert.Contains("wt=w1", folder.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GofileSaysWhenItsScriptHasNoToken()
    {
        using var web = new FakeWeb((request, response) => request.Url!.AbsolutePath switch
        {
            "/accounts" => FakeWeb.TextAsync(response, "application/json", """{"status":"ok","data":{"token":"t1"}}"""),
            _ => FakeWeb.TextAsync(response, "application/javascript", "var config = {};"),
        });
        using var api = new GofileApi(_http, UserAgent, web.Url, web.Url);
        var e = await Assert.ThrowsAsync<StageException>(() => api.ListFolderAsync("f1", CancellationToken.None));
        Assert.Contains("token", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, Ranges.Honoured)]
    [InlineData(100_000, Ranges.Honoured)]
    [InlineData(100_000, Ranges.Ignored)]
    public async Task MegaDecryptsAFileWholeAndFromWhereItStopped(int already, Ranges ranges)
    {
        var plain = MegaFixture.Bytes(300_000);
        var (rawKey, encrypted) = MegaFixture.Encrypt(plain);
        using var cdn = MegaCdn(encrypted, ranges);
        Assert.Equal(plain, await TransferAsync(cdn, rawKey, plain.Length, plain[..already]));
        Assert.Equal($"bytes={already}-{plain.Length - 1}", Assert.Single(cdn.Requests).Headers["Range"]);
    }

    [Fact]
    public async Task MegaWontCarryOnFromTheWrongByte()
    {
        var plain = MegaFixture.Bytes(300_000);
        var (rawKey, encrypted) = MegaFixture.Encrypt(plain);
        using var cdn = MegaCdn(encrypted, Ranges.Wrong);
        var e = await Assert.ThrowsAsync<StageException>(() => TransferAsync(cdn, rawKey, plain.Length, plain[..100_000]));
        Assert.False(e.IsRetryable);
    }

    [Fact]
    public async Task MegaFailsAFileThatIsNotItsKeys()
    {
        var plain = MegaFixture.Bytes(50_000);
        var (rawKey, encrypted) = MegaFixture.Encrypt(plain);
        rawKey[^1] ^= 1;
        using var cdn = MegaCdn(encrypted, Ranges.Honoured);
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var part = Path.Combine(folder, "f.ferry-part");
            var e = await Assert.ThrowsAsync<StageException>(() => new MegaTransport(_http, UserAgent, new Uri(cdn.Url, "/file"), rawKey, plain.Length).TransferAsync(Job(part), CancellationToken.None));
            Assert.False(e.IsRetryable);
            Assert.Equal(0, new FileInfo(part).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    public enum Ranges
    {
        Honoured,
        Ignored,
        Wrong,
    }

    private static FakeWeb MegaCdn(byte[] encrypted, Ranges ranges) => new(async (request, response) =>
    {
        var from = 0L;
        if (request.Headers["Range"] is { } range && ranges != Ranges.Ignored)
        {
            var asked = long.Parse(range["bytes=".Length..].Split('-')[0], CultureInfo.InvariantCulture);
            from = ranges == Ranges.Wrong && asked > 0 ? asked + 16 : asked;
            response.StatusCode = (int)HttpStatusCode.PartialContent;
            response.Headers["Content-Range"] = $"bytes {from}-{encrypted.Length - 1}/{encrypted.Length}";
        }
        await FakeWeb.BytesAsync(response, "application/octet-stream", encrypted[(int)from..]);
    });

    private async Task<byte[]> TransferAsync(FakeWeb cdn, byte[] rawKey, long size, byte[] already)
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var part = Path.Combine(folder, "f.ferry-part");
            await File.WriteAllBytesAsync(part, already);
            await new MegaTransport(_http, UserAgent, new Uri(cdn.Url, "/file"), rawKey, size).TransferAsync(Job(part), CancellationToken.None);
            return await File.ReadAllBytesAsync(part);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static TransportJob Job(string part) => new(part, _ => { }, (_, _) => ValueTask.CompletedTask, _ => { }, _ => { });
}

/// <summary>Mega's file encryption and its check, written out by the book, apart from the code under test.</summary>
internal static class MegaFixture
{
    private const int BlockBytes = 16;
    private const int ChunkStep = 128 << 10;
    private const int LargestChunk = 1 << 20;

    /// <summary>The same bytes for the same length, every time.</summary>
    public static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A key for <paramref name="plain"/>, with Mega's check in it, and <paramref name="plain"/> as Mega would store it.</summary>
    public static (byte[] RawKey, byte[] Encrypted) Encrypt(byte[] plain)
    {
        var random = new Random(plain.Length + 1);
        var (key, nonce) = (new byte[BlockBytes], new byte[8]);
        random.NextBytes(key);
        random.NextBytes(nonce);
        using var aes = Aes.Create();
        aes.Key = key;

        var encrypted = new byte[plain.Length];
        var counter = new byte[BlockBytes];
        nonce.CopyTo(counter, 0);
        for (var at = 0; at < plain.Length; at += BlockBytes)
        {
            BinaryPrimitives.WriteInt64BigEndian(counter.AsSpan(8), at / BlockBytes);
            var mask = aes.EncryptEcb(counter, PaddingMode.None);
            for (var i = 0; i < BlockBytes && at + i < plain.Length; i++)
                encrypted[at + i] = (byte)(plain[at + i] ^ mask[i]);
        }

        var fileMac = new byte[BlockBytes];
        var (offset, chunkSize) = (0, 0);
        while (offset < plain.Length)
        {
            chunkSize = Math.Min(chunkSize + ChunkStep, LargestChunk);
            var chunk = plain.AsSpan(offset, Math.Min(chunkSize, plain.Length - offset));
            var chunkMac = new byte[BlockBytes];
            nonce.CopyTo(chunkMac, 0);
            nonce.CopyTo(chunkMac, 8);
            for (var at = 0; at < chunk.Length; at += BlockBytes)
            {
                var block = new byte[BlockBytes];
                chunk.Slice(at, Math.Min(BlockBytes, chunk.Length - at)).CopyTo(block);
                chunkMac = aes.EncryptEcb(Xor(block, chunkMac), PaddingMode.None);
            }
            fileMac = aes.EncryptEcb(Xor(fileMac, chunkMac), PaddingMode.None);
            offset += chunk.Length;
        }

        var raw = new byte[32];
        nonce.CopyTo(raw, 16);
        for (var i = 0; i < 4; i++)
        {
            raw[24 + i] = (byte)(fileMac[i] ^ fileMac[4 + i]);
            raw[28 + i] = (byte)(fileMac[8 + i] ^ fileMac[12 + i]);
        }
        for (var i = 0; i < BlockBytes; i++)
            raw[i] = (byte)(key[i] ^ raw[16 + i]);
        return (raw, encrypted);
    }

    private static byte[] Xor(byte[] one, byte[] other) => [.. one.Zip(other, (a, b) => (byte)(a ^ b))];
}

/// <summary>A web server on loopback that answers as told, and remembers what it was asked.</summary>
internal sealed class FakeWeb : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _serving;

    public FakeWeb(Func<HttpListenerRequest, HttpListenerResponse, Task> answer)
    {
        (_listener, Url) = TestListener.Start();
        _serving = Task.Run(() => ServeAsync(answer));
    }

    public Uri Url { get; }

    /// <summary>Every request, in order: its path with query, and its headers.</summary>
    public ConcurrentQueue<(string Path, NameValueCollection Headers)> Requests { get; } = new();

    public static Task TextAsync(HttpListenerResponse response, string type, string text) => BytesAsync(response, type, Encoding.UTF8.GetBytes(text));

    public static async Task BytesAsync(HttpListenerResponse response, string type, byte[] bytes)
    {
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    public static Task StatusAsync(HttpListenerResponse response, HttpStatusCode status)
    {
        response.StatusCode = (int)status;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _listener.Close();
        _serving.Wait(TimeSpan.FromSeconds(5));
    }

    private async Task ServeAsync(Func<HttpListenerRequest, HttpListenerResponse, Task> answer)
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return; // closed
            }
            Requests.Enqueue((context.Request.Url!.PathAndQuery, context.Request.Headers));
            try
            {
                await answer(context.Request, context.Response);
            }
            catch (HttpListenerException)
            {
                // the client stopped reading, which some of the tests make it do
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}

/// <summary>An FTP server on loopback with one file, which says the file has <paramref name="saidSize"/> bytes whatever it sends.</summary>
internal sealed class FakeFtp : IDisposable
{
    private readonly byte[] _file;
    private readonly int _saidSize;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource _clientGone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serving;

    public FakeFtp(byte[] file, int saidSize)
    {
        (_file, _saidSize) = (file, saidSize);
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The command lines received, in order.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    /// <summary>Resolves once the client's control connection has closed.</summary>
    public Task ClientGoneAsync() => _clientGone.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public void Dispose()
    {
        _listener.Stop();
        _serving.Wait(TimeSpan.FromSeconds(5));
    }

    private async Task ServeAsync()
    {
        TcpListener? data = null;
        try
        {
            using var control = await _listener.AcceptTcpClientAsync();
            var stream = control.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await ReplyAsync(stream, "220 fake");
            var rest = 0;
            while (await reader.ReadLineAsync() is { } line)
            {
                Commands.Enqueue(line);
                var (verb, argument) = line.Split(' ', 2) is [var v, var a] ? (v, a) : (line, "");
                switch (verb)
                {
                    case "USER":
                        await ReplyAsync(stream, "331 password");
                        break;
                    case "PASS" or "TYPE":
                        await ReplyAsync(stream, "200 ok");
                        break;
                    case "SIZE":
                        await ReplyAsync(stream, $"213 {_saidSize}");
                        break;
                    case "REST":
                        rest = int.Parse(argument, CultureInfo.InvariantCulture);
                        await ReplyAsync(stream, "350 from there");
                        break;
                    case "EPSV":
                        data = new TcpListener(IPAddress.Loopback, 0);
                        data.Start();
                        await ReplyAsync(stream, $"229 Entering Extended Passive Mode (|||{((IPEndPoint)data.LocalEndpoint).Port}|)");
                        break;
                    case "RETR" when data is not null:
                        await ReplyAsync(stream, "150 sending");
                        using (var channel = await data.AcceptTcpClientAsync())
                            await channel.GetStream().WriteAsync(_file.AsMemory(rest));
                        await ReplyAsync(stream, "226 sent");
                        break;
                    default:
                        await ReplyAsync(stream, "500 what");
                        break;
                }
            }
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            // the client or the test went away
        }
        finally
        {
            data?.Stop();
            _clientGone.TrySetResult();
        }
    }

    private static async Task ReplyAsync(Stream stream, string line)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"));
        await stream.FlushAsync();
    }
}
