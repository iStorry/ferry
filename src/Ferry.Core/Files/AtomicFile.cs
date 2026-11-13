using System.Text;

namespace Ferry.Core.Files;

/// <summary>
/// Replacing a file whole: the new content goes to "{name}.tmp", reaches the disk, and is then moved over the old
/// file. A crash or a power cut at any point leaves the old file or the new one, never an empty or half-written
/// one, which a renamed file that was only journaled could otherwise be.
/// </summary>
internal static class AtomicFile
{
    private const string TempSuffix = ".tmp";

    /// <summary>Write <paramref name="text"/> as UTF-8 without a byte-order mark.</summary>
    /// <inheritdoc cref="Write"/>
    public static void WriteText(string path, string text, Func<string, FileStream>? create = null) =>
        Write(path, Encoding.UTF8.GetBytes(text), create);

    /// <param name="create">opens the temporary file at the path given, when it needs more than a plain file: a
    /// Unix mode or a Windows ACL that makes it private from the first byte</param>
    /// <exception cref="IOException">the file can't be written</exception>
    public static void Write(string path, ReadOnlySpan<byte> content, Func<string, FileStream>? create = null)
    {
        var temporary = path + TempSuffix;
        using (var stream = create?.Invoke(temporary) ?? new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Remove <paramref name="path"/>, and a temporary file a write of it that was cut short left behind.</summary>
    public static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + TempSuffix);
    }
}
