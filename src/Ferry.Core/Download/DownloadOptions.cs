using Ferry.Core.Http;
using Ferry.Core.Text;

namespace Ferry.Core.Download;

/// <summary>How hard to pull on the server: connections per file and the smallest part worth its own connection.</summary>
public enum DownloadProfile
{
    Conservative,
    Balanced,
    Aggressive,
}

/// <summary>What to do when the file to download is already in the folder.</summary>
public enum ExistingFile
{
    /// <summary>Save as "name (1).ext".</summary>
    Rename,
    Skip,
    Overwrite,
}

/// <param name="Connections">connections per file, at least 1</param>
/// <param name="MinSegmentBytes">a file is split into parts of at least this size; a smaller file gets one connection</param>
/// <param name="StallTimeout">give up on a connection that receives nothing for this long; the download as a whole
/// has no time limit</param>
/// <param name="Retry">per part: how often a connection is tried again, counted afresh whenever it made progress</param>
/// <param name="SortByType">files go into a folder for their kind (<see cref="Files.FileKinds"/>) inside the one they were
/// added for, unless the user chose that one for them</param>
public sealed record DownloadOptions(int Connections, long MinSegmentBytes, TimeSpan StallTimeout, RetryPolicy Retry,
                                     ExistingFile OnExisting, bool SortByType = false)
{
    /// <summary>Where a file goes and what it is called, from where and what it would otherwise be, when the user's rules say something else.</summary>
    public Func<string, string, (string Folder, string Name)>? Place { get; init; }

    /// <summary>
    /// Hash every file as it arrives, whether a checksum asks for it or not. The server's agent sets it, as the PC checks
    /// the transfer against this hash; a plain download doesn't pay for work nobody uses.
    /// </summary>
    public bool AlwaysHash { get; init; }

    /// <summary>The most connections per file unless the settings say otherwise.</summary>
    public const int DefaultMaxConnections = 16;

    /// <param name="connections">overrides the profile's number of connections</param>
    /// <param name="maxConnections">the most connections, whatever the profile or <paramref name="connections"/> say</param>
    public static DownloadOptions For(DownloadProfile profile, int? connections = null, ExistingFile onExisting = ExistingFile.Rename,
                                      int maxConnections = DefaultMaxConnections)
    {
        var (profileConnections, minSegment) = profile switch
        {
            DownloadProfile.Conservative => (2, 4 * Units.MiB),
            DownloadProfile.Balanced => (4, Units.MiB),
            DownloadProfile.Aggressive => (8, Units.MiB),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };
        return new DownloadOptions(Math.Clamp(connections ?? profileConnections, 1, Math.Max(1, maxConnections)), minSegment,
                                   StallTimeout: TimeSpan.FromSeconds(30),
                                   Retry: RetryPolicy.Untimed(maxRetries: 3, wait: TimeSpan.FromSeconds(1)), onExisting);
    }
}
