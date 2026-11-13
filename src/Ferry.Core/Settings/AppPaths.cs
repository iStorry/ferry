using System.Runtime.InteropServices;

namespace Ferry.Core.Settings;

/// <summary>
/// Where Ferry keeps its files: settings, the queue and logs in <see cref="ConfigFolder"/>, and downloads in
/// <see cref="DownloadFolder"/> unless a setting says otherwise.
/// </summary>
public sealed partial record AppPaths(string ConfigFolder, string DownloadFolder)
{
    public string SettingsFile => Path.Combine(ConfigFolder, "settings.json");

    public string QueueFile => Path.Combine(ConfigFolder, "queue.json");

    public string LogFolder => Path.Combine(ConfigFolder, "logs");

    /// <summary>
    /// Windows: %LOCALAPPDATA%\Ferry. Linux: $XDG_CONFIG_HOME/ferry, else ~/.config/ferry.
    /// Downloads go to "Downloads\Ferry".
    /// </summary>
    public static AppPaths ForApp()
    {
        var config = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product.Name)
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && !OperatingSystem.IsMacOS()
                               ? xdg
                               : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
                           Product.Name.ToLowerInvariant());
        return new AppPaths(config, Path.Combine(UserDownloads(), Product.Name));
    }

    /// <summary>The user's Downloads folder, wherever they moved it.</summary>
    private static string UserDownloads()
    {
        if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(DownloadsFolderId, 0, 0, out var path) == 0)
        {
            try
            {
                return Marshal.PtrToStringUni(path) ?? DefaultDownloads();
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
        return DefaultDownloads();
    }

    private static string DefaultDownloads() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    // FOLDERID_Downloads
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
