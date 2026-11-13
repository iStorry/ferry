using System.Collections.Concurrent;
using System.Security.Cryptography;
using Ferry.Core;
using Ferry.Core.Download;
using Ferry.Core.Http;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>Bringing a finished file over from the server: resume, SHA-256 check, names; against <see cref="LocalFiles"/>.</summary>
public sealed class TransferTests : IDisposable
{
    private const int MiB = 1 << 20;
    private const string Name = "clip.mp4";
    private readonly string _root = Directory.CreateTempSubdirectory("ferry_transfer_").FullName;
    private readonly LocalFiles _files = new();
    private readonly ConcurrentQueue<DownloadEvent> _events = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ServerFolder => Directory.CreateDirectory(Path.Combine(_root, "server")).FullName;

    private string PcFolder => Path.Combine(_root, "pc");

    private string Saved(string name = Name) => Path.Combine(PcFolder, name);

    private string[] PcFiles => Directory.Exists(PcFolder)
        ? [.. Directory.GetFiles(PcFolder).Select(Path.GetFileName).OfType<string>().Order()]
        : [];

    private (RemoteFile File, byte[] Data) ServerFile(int size = 3 * MiB, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        var path = Path.Combine(ServerFolder, Name);
        File.WriteAllBytes(path, data);
        return (new RemoteFile(Guid.NewGuid(), "clip", path.Replace('\\', '/'), size, Convert.ToHexStringLower(SHA256.HashData(data))), data);
    }

    private Task<DownloadResult> TransferAsync(RemoteFile file, ExistingFile onExisting = ExistingFile.Rename,
                                               int connections = 1, CancellationToken cancel = default) =>
        new FileTransfer(_files, new TransferOptions(RetryPolicy.Untimed(3, TimeSpan.FromMilliseconds(1)), TimeSpan.FromSeconds(5),
                                                     onExisting, Connections: connections, MinSegmentBytes: 4 << 20))
            .RunAsync(file, PcFolder, _events.Enqueue, cancel);

    [Fact]
    public async Task FileComesOverAndIsChecked()
    {
        var (file, data) = ServerFile();

        var result = await TransferAsync(file);

        Assert.Equal(new DownloadResult(Saved(), Skipped: false, file.Sha256), result);
        Assert.Equal(data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal([Name], PcFiles);
        Assert.Equal([0L], _files.Opened);
        Assert.Equal([3L * MiB], _files.Lengths); // the whole file, asked for as one part
        Assert.Equal(new DownloadFinished(Saved(), 3 * MiB), _events.OfType<DownloadFinished>().Single());
    }

    [Fact]
    public async Task FileComesOverInPartsOnSeveralConnections()
    {
        var (file, data) = ServerFile(20 * MiB);

        await TransferAsync(file, connections: 4);

        Assert.Equal(data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal([Name], PcFiles);
        // four parts, each asked for with its own length; which connection reads which depends on timing
        Assert.Subset(_files.Opened.ToHashSet(), new HashSet<long> { 0L, 5L * MiB, 10L * MiB, 15L * MiB });
        Assert.All(_files.Lengths, length => Assert.InRange(length, 1, 5L * MiB));
        Assert.Equal(4, _events.OfType<DownloadStarted>().Single().Connections);
    }

    [Fact]
    public async Task PartsContinueInALaterRun()
    {
        var (file, data) = ServerFile(20 * MiB);
        _files.HoldAt = 2 * MiB; // the first part stops there; the others start past it and wait
        using var cancel = new CancellationTokenSource();
        var first = TransferAsync(file, cancel: cancel.Token, connections: 4);
        await _files.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => _files.Opened.Count == 4);
        await Task.Delay(250); // what was read before the hold gets to the file
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        _files.HoldAt = null;
        _files.Release();
        _events.Clear();

        var result = await TransferAsync(file, connections: 4);

        Assert.Equal(data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal(file.Sha256, result.Sha256); // hashed as it came, the part already there first
        var started = _events.OfType<DownloadStarted>().Single();
        Assert.True(started.Resumed);
        // what was on disk when it was cancelled: a chunk still on its way when that happened is fetched again
        Assert.InRange(started.Done, 1, 2L * MiB);
    }

    [Fact]
    public async Task DroppedConnectionContinuesWhereItStopped()
    {
        var (file, data) = ServerFile();
        _files.DropOnceAt = MiB + 100;

        await TransferAsync(file);

        Assert.Equal(data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal([0L, MiB + 100L], _files.Opened);
        Assert.Single(_events.OfType<DownloadRetrying>());
    }

    [Fact]
    public async Task InterruptedTransferContinuesInALaterRun()
    {
        var (file, data) = ServerFile();
        _files.HoldAt = 2 * MiB;
        using var cancel = new CancellationTokenSource();
        var first = TransferAsync(file, cancel: cancel.Token);
        await _files.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(["clip.mp4.ferry-part", "clip.mp4.ferry-part.json"], PcFiles);
        _files.HoldAt = null;
        _files.Release();
        _events.Clear();

        await TransferAsync(file);

        Assert.Equal(data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal([0L, 2L * MiB], _files.Opened);
        var started = _events.OfType<DownloadStarted>().Single();
        Assert.True(started.Resumed);
        Assert.Equal(2L * MiB, started.Done);
    }

    [Fact]
    public async Task MismatchedFileComesOverOnceMore()
    {
        var (file, data) = ServerFile();
        _files.CorruptOpens = 1;

        await TransferAsync(file);

        Assert.Equal(data, await File.ReadAllBytesAsync(Saved()));
        Assert.Single(_events.OfType<DownloadRestarted>());
        Assert.Equal([0L, 0L], _files.Opened);
    }

    [Fact]
    public async Task FileThatNeverMatchesFailsAndLeavesNothing()
    {
        var (file, _) = ServerFile();
        _files.CorruptOpens = 2;

        var failure = await Assert.ThrowsAsync<StageException>(() => TransferAsync(file));

        Assert.Equal(Stage.Transfer, failure.Stage);
        Assert.Contains("SHA-256", failure.Message, StringComparison.Ordinal);
        Assert.Empty(PcFiles);
    }

    [Fact]
    public async Task ExistingFileIsRenamedOrSkipped()
    {
        var (file, data) = ServerFile();
        Directory.CreateDirectory(PcFolder);
        await File.WriteAllTextAsync(Saved(), "mine");

        var skipped = await TransferAsync(file, ExistingFile.Skip);
        var renamed = await TransferAsync(file, ExistingFile.Rename);

        Assert.True(skipped.Skipped);
        Assert.Equal("mine", await File.ReadAllTextAsync(Saved()));
        Assert.Equal(Saved("clip (1).mp4"), renamed.Path);
        Assert.Equal(data, await File.ReadAllBytesAsync(renamed.Path));
    }

    [Fact]
    public async Task AFileAlreadyBroughtOverDoesNotComeAgain()
    {
        var (file, data) = ServerFile();
        var first = await TransferAsync(file); // and the server never heard
        _files.Opened.Clear();
        _events.Clear();

        var again = await TransferAsync(file);

        Assert.Equal(new DownloadResult(first.Path, Skipped: false, file.Sha256), again);
        Assert.Equal([Name], PcFiles);
        Assert.Empty(_files.Opened);
        Assert.Equal(new DownloadStarted(Saved(), data.Length, data.Length, 1, true), _events.OfType<DownloadStarted>().Single());
        Assert.Equal(new DownloadFinished(Saved(), data.Length), _events.OfType<DownloadFinished>().Single());
    }

    [Fact]
    public async Task AFileBroughtOverBesideAnotherOfItsNameDoesNotComeAgain()
    {
        var (file, _) = ServerFile();
        Directory.CreateDirectory(PcFolder);
        await File.WriteAllBytesAsync(Saved(), new byte[file.Size]); // the same size, and someone else's
        var first = await TransferAsync(file);
        Assert.Equal(Saved("clip (1).mp4"), first.Path);
        _files.Opened.Clear();

        var again = await TransferAsync(file);

        Assert.Equal(first.Path, again.Path);
        Assert.Empty(_files.Opened);
        Assert.Equal(["clip (1).mp4", Name], PcFiles);
    }

    [Fact]
    public async Task APartCutShortStartsAfreshRatherThanBeingFilledWithZeros()
    {
        var (file, data) = ServerFile();
        _files.HoldAt = 2 * MiB;
        using var cancel = new CancellationTokenSource();
        var first = TransferAsync(file, cancel: cancel.Token);
        await _files.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await using (var part = new FileStream(PartFile.PartPath(Saved()), FileMode.Open, FileAccess.Write))
            part.SetLength(MiB);
        _files.HoldAt = null;
        _files.Release();
        _events.Clear();

        await TransferAsync(file);

        Assert.Equal(data, await File.ReadAllBytesAsync(Saved()));
        Assert.Single(_events.OfType<DownloadRestarted>());
        Assert.False(_events.OfType<DownloadStarted>().Single().Resumed);
        Assert.Equal(0L, _files.Opened.Last());
    }

    [Fact]
    public async Task WaitingForAReadToOpenIsNoStall()
    {
        var (file, data) = ServerFile();
        _files.OpenWait = TimeSpan.FromMilliseconds(900);
        var options = new TransferOptions(RetryPolicy.Untimed(0, TimeSpan.FromMilliseconds(1)), TimeSpan.FromMilliseconds(300), ExistingFile.Rename);

        await new FileTransfer(_files, options).RunAsync(file, PcFolder, _events.Enqueue, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Saved()));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            Assert.True(waited < 10_000, "timed out");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task PartOfAnotherVersionStartsOver()
    {
        var (file, _) = ServerFile(seed: 1);
        _files.HoldAt = MiB;
        using var cancel = new CancellationTokenSource();
        var first = TransferAsync(file, cancel: cancel.Token);
        await _files.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        _files.HoldAt = null;
        _files.Release();
        var (changed, data) = ServerFile(seed: 2);

        await TransferAsync(changed);

        Assert.Equal(data, await File.ReadAllBytesAsync(Saved()));
        Assert.Equal(0L, _files.Opened.Last());
    }
}
