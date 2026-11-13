using System.Formats.Tar;
using Ferry.Core;
using Ferry.Core.Download;
using Ferry.Core.Sites;
using MonoTorrent;
using MonoTorrent.Client;

namespace Ferry.Plugins.Torrents;

/// <summary>
/// One torrent in the one engine of this process (<see cref="TorrentEngine"/>), its files in the part as a folder. It stops,
/// and so stops sharing, as soon as what was wanted is complete. A torrent of one file then becomes that file; one of many stays a folder, or on a server
/// becomes one .tar, as a server's files go on to the PC one at a time.
/// </summary>
/// <param name="chosen">the places of the files wanted, or null for all</param>
internal sealed class TorrentTransport(TorrentTransport.Adding add, IReadOnlySet<int>? chosen, long downloadLimit, long uploadLimit, bool packFolders) : IFileTransport
{
    /// <summary>Puts the torrent in the engine, to save in a folder.</summary>
    internal delegate Task<TorrentManager> Adding(ClientEngine engine, string folder, TorrentSettings settings);

    private const string TarEnding = ".tar";
    private const double AllOfIt = 100;

    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

    public async Task TransferAsync(TransportJob job, CancellationToken cancel)
    {
        Directory.CreateDirectory(job.PartPath);
        var settings = new TorrentSettingsBuilder
        {
            CreateContainingDirectory = false,
            MaximumDownloadRate = Rate(downloadLimit),
            MaximumUploadRate = Rate(uploadLimit),
        }.ToSettings();
        await using var lease = TorrentEngine.Lease();
        var engine = lease.Engine;
        TorrentManager manager;
        try
        {
            manager = await add(engine, job.PartPath, settings);
        }
        catch (TorrentException e)
        {
            throw new StageException(Stage.Download, $"The torrent couldn't be started: {e.Message}", inner: e);
        }
        try
        {
            await manager.StartAsync();
            await WatchAsync(manager, job, cancel);
        }
        finally
        {
            await manager.StopAsync(StopWait);
            await engine.RemoveAsync(manager, RemoveMode.CacheDataOnly);
        }
        PartFile.BecomeOneFile(job.PartPath);
        if (packFolders && Directory.Exists(job.PartPath))
            await PackAsync(job, cancel);
    }

    private async Task WatchAsync(TorrentManager manager, TransportJob job, CancellationToken cancel)
    {
        using var ticks = new PeriodicTimer(ProgressEvery);
        var size = 0L;
        var wanted = false;
        while (true)
        {
            if (manager.State == TorrentState.Error)
                throw new StageException(Stage.Download, $"The torrent stopped: {manager.Error?.Exception.Message}", inner: manager.Error?.Exception);
            if (manager.Torrent is { } torrent)
            {
                if (!wanted)
                {
                    size = await ChooseAsync(manager, torrent);
                    job.OnTotal(size);
                    wanted = true;
                }
                job.OnBytes((long)(size * manager.PartialProgress / AllOfIt));
                if (manager.PartialProgress >= AllOfIt)
                    return;
            }
            await ticks.WaitForNextTickAsync(cancel);
        }
    }

    /// <summary>Leave out the files not wanted; the size of those that are.</summary>
    private async Task<long> ChooseAsync(TorrentManager manager, Torrent torrent)
    {
        var size = 0L;
        for (var i = 0; i < manager.Files.Count; i++)
        {
            if (chosen is null || chosen.Contains(i))
                size += manager.Files[i].Length;
            else
                await manager.SetFilePriorityAsync(manager.Files[i], Priority.DoNotDownload);
        }
        return chosen is null ? torrent.Size : size;
    }

    private static async Task PackAsync(TransportJob job, CancellationToken cancel)
    {
        var packed = job.PartPath + TarEnding;
        await using (var file = File.Create(packed))
            await TarFile.CreateFromDirectoryAsync(job.PartPath, file, includeBaseDirectory: false, cancel);
        Directory.Delete(job.PartPath, recursive: true);
        File.Move(packed, job.PartPath);
        job.OnEnding(TarEnding);
    }

    private static int Rate(long bytesPerSecond) => (int)Math.Clamp(bytesPerSecond, 0, int.MaxValue);
}
