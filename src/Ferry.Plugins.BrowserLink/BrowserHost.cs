using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.BrowserLink;

/// <summary>The browser's end of Browser Link: Chrome and Edge start the CLI with the extension's origin, and speak length-prefixed JSON on its stdin.</summary>
public static class BrowserHost
{
    private const string OriginPrefix = "chrome-extension://";
    private const int MostMessageBytes = 1 << 20;
    private const string MagnetScheme = "magnet";

    /// <summary>The CLI was started by a browser, for the extension.</summary>
    public static bool IsStartedFor(string[] args) => args is [var origin, ..] && origin.StartsWith(OriginPrefix, StringComparison.Ordinal);

    /// <summary>Takes one message and gives it to the app, which is started if need be.</summary>
    /// <param name="app">the app's program</param>
    public static async Task<int> RunAsync(string[] args, Stream input, Stream output, string app, CancellationToken cancel)
    {
        if (!BrowserLinkPlugin.ExtensionIds.Any(id => args[0].TrimEnd('/') == OriginPrefix + id))
            return 1;
        var message = await ReadFrameAsync(input, cancel);
        var answer = "{\"ok\":false}";
        if (message is not null && IsWebLink(message))
        {
            var id = Guid.NewGuid().ToString("N");
            BrowserLinkPlugin.SweepHandoffs();
            BrowserLinkPlugin.Handoffs.Save(id, message);
            using var started = Process.Start(new ProcessStartInfo(app, ["--from-browser", id]) { UseShellExecute = false });
            answer = "{\"ok\":true}";
        }
        await WriteFrameAsync(output, Encoding.UTF8.GetBytes(answer), cancel);
        return 0;
    }

    /// <summary>What the extension sent: the link, and for its site the page it was on and the cookies.</summary>
    /// <exception cref="JsonException">it isn't such a message</exception>
    public static (string Link, RequestDetails Details) ReadMessage(byte[] message)
    {
        using var json = JsonDocument.Parse(message);
        var root = json.RootElement;
        var link = root.GetProperty("url").GetString() ?? throw new JsonException("no link");
        var referer = root.TryGetProperty("referer", out var from) ? from.GetString() : null;
        var cookies = root.TryGetProperty("cookies", out var given) ? given.GetString() : null;
        return (link, RequestDetails.TryCreate(referer, cookies, [], out var details, out _) ? details : RequestDetails.None);
    }

    /// <summary>The message names a link the app can take: a web link, or a magnet link.</summary>
    private static bool IsWebLink(byte[] message)
    {
        try
        {
            return Uri.TryCreate(ReadMessage(message).Link, UriKind.Absolute, out var link) && (LinkProbe.IsWebLink(link) || link.Scheme == MagnetScheme);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream input, CancellationToken cancel)
    {
        var header = new byte[sizeof(int)];
        await input.ReadExactlyAsync(header, cancel);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MostMessageBytes)
            return null;
        var body = new byte[length];
        await input.ReadExactlyAsync(body, cancel);
        return body;
    }

    private static async Task WriteFrameAsync(Stream output, byte[] body, CancellationToken cancel)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await output.WriteAsync(header, cancel);
        await output.WriteAsync(body, cancel);
        await output.FlushAsync(cancel);
    }
}
