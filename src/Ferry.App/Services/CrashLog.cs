using System.Globalization;
using Ferry.Core;

namespace Ferry.App.Services;

/// <summary>
/// Exceptions nothing else caught, written to crash.log beside the settings file (the newest last, the file kept
/// small), so a crash can be traced afterwards. Writing it never throws.
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 1 << 20;
    private static readonly Lock Writing = new();

    /// <summary>Set once the settings folder is known; until then the default one is used.</summary>
    public static string? Folder { get; set; }

    public static string FilePath => Path.Combine(
        Folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product.Name), "crash.log");

    public static void Write(string where, Exception? error)
    {
        try
        {
            lock (Writing)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Delete(path);
                File.AppendAllText(path, string.Create(CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {Product.Build} {where}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}"));
            }
        }
#pragma warning disable CA1031 // the log is the last resort: failing to write it must not add a second failure
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
