using System.Formats.Tar;
using System.IO.Compression;
using Ferry.Plugins.AfterDownload;

namespace Ferry.Core.Tests;

/// <summary>Unpacking as the After Download plugin does it, with the room it is given.</summary>
public sealed class AfterDownloadTests
{
    private const int EntryBytes = 1 << 20;

    [Theory]
    [InlineData(".tar")]
    [InlineData(".tgz")]
    public async Task ATarThatWouldUnpackToMoreThanThereIsRoomForIsStopped(string ending)
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var archive = Path.Combine(folder, "set" + ending);
            await WriteTarAsync(archive, gzip: ending == ".tgz");
            var into = Path.Combine(folder, "set");

            var e = await Assert.ThrowsAsync<IOException>(() => FinishedFileWork.UnpackAsync(archive, into, sevenZip: "", room: EntryBytes / 2, CancellationToken.None));
            Assert.Contains("room", e.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(into), "a refused archive leaves nothing behind");

            await FinishedFileWork.UnpackAsync(archive, into, sevenZip: "", room: 2 * EntryBytes, CancellationToken.None);
            Assert.Equal(EntryBytes, new FileInfo(Path.Combine(into, "big.bin")).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AZipIsRefusedByWhatItsListingSays()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var archive = Path.Combine(folder, "set.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            await using (var entry = zip.CreateEntry("big.bin").Open())
                await entry.WriteAsync(new byte[EntryBytes]);
            var into = Path.Combine(folder, "set");
            await Assert.ThrowsAsync<IOException>(() => FinishedFileWork.UnpackAsync(archive, into, sevenZip: "", room: EntryBytes / 2, CancellationToken.None));
            Assert.False(Directory.Exists(into));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task WriteTarAsync(string path, bool gzip)
    {
        await using var file = File.Create(path);
        await using Stream packed = gzip ? new GZipStream(file, CompressionLevel.Fastest) : file;
        await using var tar = new TarWriter(packed);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, "big.bin") { DataStream = new MemoryStream(new byte[EntryBytes]) };
        await tar.WriteEntryAsync(entry);
    }
}
