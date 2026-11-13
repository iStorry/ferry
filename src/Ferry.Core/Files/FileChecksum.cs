using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Ferry.Core.Files;

/// <summary>The SHA-256 of a file, and checking a finished one against the SHA-256 its source publishes.</summary>
public static class FileChecksum
{
    private const int Sha256HexLength = 64;

    // a read at a time: enough to keep a disk busy, small enough to come from the pool
    private const int ChunkBytes = 1 << 20;

    /// <summary>What a person typed, in the form <see cref="MatchesAsync"/> takes; null for nothing typed, false for what isn't a SHA-256.</summary>
    public static bool TryRead(string? text, out string? sha256)
    {
        var trimmed = text?.Trim() ?? "";
        sha256 = trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
        return trimmed.Length == 0 || (trimmed.Length == Sha256HexLength && trimmed.All(char.IsAsciiHexDigit));
    }

    /// <exception cref="IOException">the file can't be read</exception>
    public static async Task<bool> MatchesAsync(string path, string sha256, CancellationToken cancel = default) =>
        await Sha256Async(path, cancel) == sha256;

    /// <summary>The SHA-256 of the file at <paramref name="path"/>, in lower-case hex.</summary>
    /// <exception cref="IOException">the file can't be read</exception>
    public static async Task<string> Sha256Async(string path, CancellationToken cancel = default)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await Sha256Async(handle, RandomAccess.GetLength(handle), cancel);
    }

    /// <summary>The SHA-256 of the first <paramref name="length"/> bytes of the file open on <paramref name="handle"/>, in lower-case hex.</summary>
    /// <exception cref="IOException">the file can't be read, or is shorter than <paramref name="length"/></exception>
    public static async Task<string> Sha256Async(SafeFileHandle handle, long length, CancellationToken cancel = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await AppendAsync(hash, handle, 0, length, cancel);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Feed bytes [<paramref name="from"/>, <paramref name="to"/>) of the file open on <paramref name="handle"/> to <paramref name="hash"/>.</summary>
    /// <exception cref="IOException">the file can't be read, or ends before <paramref name="to"/></exception>
    internal static async Task AppendAsync(IncrementalHash hash, SafeFileHandle handle, long from, long to, CancellationToken cancel)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            for (var offset = from; offset < to;)
            {
                var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, (int)Math.Min(ChunkBytes, to - offset)), offset, cancel);
                if (read == 0)
                    throw new IOException("The file is shorter than it should be.");
                hash.AppendData(buffer, 0, read);
                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
