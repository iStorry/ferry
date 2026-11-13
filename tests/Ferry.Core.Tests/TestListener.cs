using System.Net;
using System.Net.Sockets;

namespace Ferry.Core.Tests;

/// <summary>A web server's listener on a free port: tests run at once, and another can take the port first.</summary>
internal static class TestListener
{
    private const int Tries = 20;

    public static (HttpListener Listener, Uri Url) Start()
    {
        for (var attempt = 1; ; attempt++)
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var url = new Uri($"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}/");
            socket.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add(url.ToString());
            try
            {
                listener.Start();
                return (listener, url);
            }
            catch (HttpListenerException) when (attempt < Tries)
            {
                ((IDisposable)listener).Dispose();
            }
        }
    }
}
