using MonoTorrent.Client;

namespace Ferry.Plugins.Torrents;

/// <summary>
/// The one engine of this process: there while a torrent is being downloaded or asked about, and gone once none is, so
/// its listener, DHT and local peer discovery don't run on for nothing. Nothing is kept that would say what was
/// downloaded: a resumed torrent finds its metadata again, and checks what it has. Nothing on the user's router changes
/// without being asked either.
/// </summary>
internal static class TorrentEngine
{
    private static readonly Lock Gate = new();
    private static ClientEngine? _engine;
    private static int _leases;

    /// <summary>The engine, for as long as the lease is held; the last lease let go stops it.</summary>
    public static EngineLease Lease()
    {
        lock (Gate)
        {
            _engine ??= new ClientEngine(new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(Path.GetTempPath(), "ferry-torrents"),
                AllowPortForwarding = false,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = false,
            }.ToSettings());
            _leases++;
            return new EngineLease(_engine);
        }
    }

    private static async ValueTask ReleaseAsync()
    {
        ClientEngine? last;
        lock (Gate)
        {
            last = --_leases == 0 ? _engine : null;
            if (last is not null)
                _engine = null;
        }
        if (last is null)
            return;
        await last.StopAllAsync();
        last.Dispose();
    }

    internal sealed class EngineLease(ClientEngine engine) : IAsyncDisposable
    {
        public ClientEngine Engine => engine;

        public ValueTask DisposeAsync() => ReleaseAsync();
    }
}
