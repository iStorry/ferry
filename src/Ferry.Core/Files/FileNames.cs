using System.Globalization;
using System.Text;

namespace Ferry.Core.Files;

public static class FileNames
{
    /// <summary>Used when nothing is left of a name.</summary>
    public const string Fallback = "download";

    /// <summary>Longest name in UTF-8 bytes: inside ext4's 255-byte and NTFS's 255-character limits, with room for " (12)" and ".ferry-part".</summary>
    public const int MaxBytes = 200;

    /// <summary>Longer than this is not an extension worth keeping whole ("archive.part-of-a-long-title").</summary>
    private const int MaxExtensionChars = 16;

    private static readonly HashSet<string> ReservedOnWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A name that can be saved on Windows, macOS and Linux alike: no path separators or characters Windows refuses,
    /// not a reserved device name, no trailing dots or spaces, at most <see cref="MaxBytes"/> UTF-8 bytes with the
    /// extension kept.
    /// </summary>
    public static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (IsDirectionControl(c))
                continue; // they make a name read differently from what it is
            builder.Append(char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : c);
        }
        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0)
            return Fallback;

        var dot = cleaned.LastIndexOf('.');
        var hasExtension = dot > 0 && cleaned.Length - dot <= MaxExtensionChars;
        var stem = hasExtension ? cleaned[..dot] : cleaned;
        var extension = hasExtension ? cleaned[dot..] : "";
        if (ReservedOnWindows.Contains(stem.Split('.')[0]))
            stem = "_" + stem;
        return Shorten(stem, MaxBytes - Encoding.UTF8.GetByteCount(extension)).TrimEnd('.', ' ') + extension;
    }

    /// <summary>
    /// <paramref name="path"/>, then "name (1).ext", "name (2).ext", ... for the caller to pick the first free one
    /// (what counts as free depends on the caller: a download also keeps the names of its part files).
    /// </summary>
    public static IEnumerable<string> Candidates(string path)
    {
        yield return path;
        var folder = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; ; i++)
            yield return Path.Combine(folder, $"{stem} ({i}){extension}");
    }

    private static bool IsDirectionControl(char c) => c is '\u200E' or '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';

    /// <summary>The longest start of <paramref name="text"/> that fits in <paramref name="maxBytes"/>, cut between characters.</summary>
    private static string Shorten(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;
        var end = 0;
        var bytes = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            bytes += Encoding.UTF8.GetByteCount(element);
            if (bytes > maxBytes)
                break;
            end += element.Length;
        }
        return text[..end];
    }
}
