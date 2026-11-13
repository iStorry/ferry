using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Tools;

namespace Ferry.Plugins.AfterDownload;

/// <summary>What happened to a finished file; null when nothing is worth saying.</summary>
public sealed record WorkResult(string Title, string Message);

/// <summary>Scans a finished file and unpacks it, as asked. A file it can't work on stays as it is.</summary>
public static partial class FinishedFileWork
{
    private const int ThreatFound = 2;

    // an archive whose contents would take more than this of the space left is refused, as a zip bomb would
    private const double MostOfFreeSpace = 0.9;
    private const string NoRoom = "It would unpack to more than there is room for.";
    private const string SevenZipSize = "Size = ";

    // "-rw-r--r--  0 user group     1234 Oct 31 12:00 name": the size is the fifth word of tar's listing
    private const int TarSizeWord = 4;

    private static readonly TimeSpan ScanLimit = TimeSpan.FromMinutes(30);

    /// <param name="sevenZip">7-Zip's program, for what .NET can't read</param>
    public static async Task<WorkResult?> RunAsync(string path, bool scan, bool extract, bool deleteArchive, string sevenZip, CancellationToken cancel)
    {
        var name = Path.GetFileName(path);
        if (scan && await ScanAsync(path, cancel) is { } scanned)
            return scanned;
        if (!extract || !IsArchive(path) || IsLaterVolume(path))
            return null;
        try
        {
            var folder = FreeFolder(path);
            await UnpackAsync(path, folder, sevenZip, Room(folder), cancel);
            if (deleteArchive)
                File.Delete(path);
            return new WorkResult("Unpacked", $"{name} is in {Path.GetFileName(folder)}.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or StageException)
        {
            return new WorkResult("Couldn't unpack", $"{name}: {e.Message}");
        }
    }

    /// <summary>Asks Defender about the file; only a threat, or failing to ask, is news.</summary>
    private static async Task<WorkResult?> ScanAsync(string path, CancellationToken cancel)
    {
        var tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-Scan", "-ScanType", "3", "-File", path, "-DisableRemediation" })
            start.ArgumentList.Add(argument);
        var name = Path.GetFileName(path);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new Win32Exception("Defender didn't start.");
        }
        catch (Win32Exception e)
        {
            return new WorkResult("Not scanned", $"{name}: {e.Message}");
        }
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(ScanLimit);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                if (cancel.IsCancellationRequested)
                    throw;
                return new WorkResult("Not scanned", $"{name}: Defender took longer than {ScanLimit.TotalMinutes:0} minutes.");
            }
            return process.ExitCode == ThreatFound ? new WorkResult("Threat found", $"Defender found a threat in {name}. It wasn't unpacked. Don't open it.") : null;
        }
    }

    internal static bool IsArchive(string path) => IsZip(path) || IsTar(path) || NeedsTool(path);

    private static bool NeedsTool(string path) => path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);

    /// <summary>The second volume on of a split archive: the first one unpacks them all.</summary>
    internal static bool IsLaterVolume(string path) => LaterVolume().IsMatch(Path.GetFileName(path));

    private static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsTar(string path) => path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) || IsGzippedTar(path);

    private static bool IsGzippedTar(string path) =>
        path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    /// <summary>A folder beside the file, by the file's name, that isn't there yet.</summary>
    internal static string FreeFolder(string path)
    {
        var stem = IsGzippedTar(path) && path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? path[..^".tar.gz".Length]
            : Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path));
        return FileNames.Candidates(stem).First(candidate => !Directory.Exists(candidate) && !File.Exists(candidate));
    }

    /// <summary>How much an archive may unpack to in <paramref name="folder"/>: most of the space left on its drive.</summary>
    private static long Room(string folder) => (long)(new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace * MostOfFreeSpace);

    /// <summary>
    /// Unpack <paramref name="path"/> into <paramref name="folder"/>, refusing one that would take more than <paramref name="room"/>
    /// bytes: a zip's and a 7-Zip or RAR archive's listing says beforehand, a tar is stopped as it goes past. The libraries and tools
    /// used here keep every entry inside the folder, whatever its name says; a failure part-way leaves nothing behind.
    /// </summary>
    /// <exception cref="IOException">the archive is damaged, or too large for the room</exception>
    /// <exception cref="StageException">7-Zip or tar isn't there, or stopped with an error</exception>
    internal static async Task UnpackAsync(string path, string folder, string sevenZip, long room, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        try
        {
            if (NeedsTool(path))
            {
                await UnpackWithToolAsync(path, folder, sevenZip, room, cancel);
            }
            else if (IsZip(path))
            {
                using (var zip = ZipFile.OpenRead(path))
                    RefuseIfTooLarge(zip.Entries.Sum(entry => entry.Length), room);
                await Task.Run(() => ZipFile.ExtractToDirectory(path, folder), cancel);
            }
            else
            {
                await using var file = File.OpenRead(path);
                await using Stream unpacked = IsGzippedTar(path) ? new GZipStream(file, CompressionMode.Decompress) : file;
                await using var tar = new CappedStream(unpacked, room);
                await TarFile.ExtractToDirectoryAsync(tar, folder, overwriteFiles: false, cancel);
            }
        }
        catch
        {
            Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    /// <summary>7-Zip if it is there, else the tar in Windows. Both say what is in an archive before they unpack it.</summary>
    private static async Task UnpackWithToolAsync(string path, string folder, string sevenZip, long room, CancellationToken cancel)
    {
        var useSevenZip = File.Exists(sevenZip);
        var tool = useSevenZip ? sevenZip : Path.Combine(Environment.SystemDirectory, "tar.exe");
        var ifMissing = useSevenZip ? "Set where 7-Zip is in After Download's options." : "Installing 7-Zip may help.";
        var unpacked = 0L;
        await ToolProcess.RunAsync(tool, useSevenZip ? ["l", "-slt", "--", path] : ["-tvf", path],
                                   line => unpacked += useSevenZip ? SevenZipEntrySize(line) : TarEntrySize(line), Stage.Download, cancel, ifMissing);
        RefuseIfTooLarge(unpacked, room);
        await ToolProcess.RunAsync(tool, useSevenZip ? ["x", "-y", $"-o{folder}", "--", path] : ["-xf", path, "-C", folder], onStdoutLine: null, Stage.Download, cancel, ifMissing);
    }

    /// <summary>The size of the entry a line of "7z l -slt" describes; 0 for a line that is something else.</summary>
    private static long SevenZipEntrySize(string line) =>
        line.StartsWith(SevenZipSize, StringComparison.Ordinal) && long.TryParse(line.AsSpan(SevenZipSize.Length), CultureInfo.InvariantCulture, out var size) ? size : 0;

    /// <summary>The size of the entry a line of "tar -tv" describes; 0 for a line that is something else.</summary>
    private static long TarEntrySize(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > TarSizeWord && long.TryParse(words[TarSizeWord], CultureInfo.InvariantCulture, out var size) ? size : 0;
    }

    private static void RefuseIfTooLarge(long bytes, long room)
    {
        if (bytes > room)
            throw new IOException(NoRoom);
    }

    /// <summary>A stream read through, which stops with <see cref="NoRoom"/> once more than its room has gone by.</summary>
    private sealed class CappedStream(Stream inner, long room) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return _read > room ? throw new IOException(NoRoom) : read;
        }
    }

    [GeneratedRegex(@"\.part0*[2-9]\d*\.rar$|\.part0*1\d+\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex LaterVolume();
}
