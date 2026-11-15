using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ferry.Plugins.Server;

/// <summary>
/// A SOCKS proxy on this PC's loopback whose connections leave from the server: one run of ssh with a dynamic forward
/// and no command (<see cref="ServerConnection.OpenTunnelAsync"/>). It is for the browser window a person passes a
/// site's check in, which the site must see coming from the server, and it is open only while that window is.
/// Anything on this PC could use the port meanwhile, as it could any proxy on loopback.
/// </summary>
public sealed class ServerTunnel : IAsyncDisposable
{
    private static readonly TimeSpan ListenWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReachWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(150);
    private readonly SshProcess _ssh;
    private readonly Askpass? _askpass;

    internal ServerTunnel(SshProcess ssh, Askpass? askpass, int port) => (_ssh, _askpass, Port) = (ssh, askpass, port);

    /// <summary>The proxy's port on 127.0.0.1.</summary>
    public int Port { get; }

    /// <summary>A port on loopback nothing listens on right now.</summary>
    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Wait until ssh has logged in and listens; false when it ended first (its stderr says why) or took too long.</summary>
    internal async Task<bool> ListensAsync(CancellationToken cancel)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        wait.CancelAfter(ListenWait);
        try
        {
            while (!_ssh.Exited.IsCompleted)
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await probe.ConnectAsync(IPAddress.Loopback, Port, wait.Token);
                    return true;
                }
                catch (SocketException)
                {
                    await Task.Delay(PollEvery, wait.Token); // not yet: it is still logging in
                }
            }
            return false;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the proxy at <paramref name="port"/> opens a connection to <paramref name="host"/>'s https port: one
    /// SOCKS5 request, by name, so the server looks the name up as it will for the browser.
    /// </summary>
    internal static async Task<bool> ReachesAsync(int port, string host, CancellationToken cancel)
    {
        var name = Encoding.ASCII.GetBytes(host);
        if (name.Length is 0 or > 255)
            return false;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        wait.CancelAfter(ReachWait);
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port, wait.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, wait.Token); // version 5, one method: no login
            var answer = new byte[4];
            await stream.ReadExactlyAsync(answer.AsMemory(0, 2), wait.Token);
            if (answer[0] != 5 || answer[1] != 0)
                return false;
            // connect, to a name, port 443
            byte[] request = [5, 1, 0, 3, (byte)name.Length, .. name, 443 >> 8, 443 & 0xFF];
            await stream.WriteAsync(request, wait.Token);
            await stream.ReadExactlyAsync(answer, wait.Token);
            return answer[0] == 5 && answer[1] == 0;
        }
        catch (Exception e) when (e is SocketException or IOException or EndOfStreamException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Close the proxy: ssh is killed, as it has nothing to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        _ssh.Kill();
        await _ssh.DisposeAsync();
        if (_askpass is not null)
            await _askpass.DisposeAsync();
    }
}
