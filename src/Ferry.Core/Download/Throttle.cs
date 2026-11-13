namespace Ferry.Core.Download;

/// <summary>The speed limits one download is under: the one shared by all downloads, and its own.</summary>
internal readonly record struct Throttle(SpeedLimit? Overall, SpeedLimit? File)
{
    public int ChunkFor(int max) => Math.Min(Overall?.ChunkFor(max) ?? max, File?.ChunkFor(max) ?? max);

    public async ValueTask WaitAsync(int bytes, CancellationToken cancel)
    {
        if (File is not null)
            await File.WaitAsync(bytes, cancel);
        if (Overall is not null)
            await Overall.WaitAsync(bytes, cancel);
    }
}
