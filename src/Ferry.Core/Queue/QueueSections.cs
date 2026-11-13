namespace Ferry.Core.Queue;

/// <summary>Where a file shows on the Downloads page, by what it needs: the user, a slot, or nothing more.</summary>
public enum QueueSection
{
    /// <summary>Failed: retrying, or removing, is the user's to decide.</summary>
    NeedsYou,

    /// <summary>Downloading here or on the server, or coming over.</summary>
    Now,

    /// <summary>In line for a slot.</summary>
    UpNext,

    /// <summary>Paused or cancelled by the user, kept until resumed or retried.</summary>
    Stopped,

    Done,
}

public static class QueueSections
{
    public static QueueSection Of(QueueItem item) => item.State switch
    {
        QueueState.Failed => QueueSection.NeedsYou,
        QueueState.Resolving or QueueState.Downloading or QueueState.OnServer or QueueState.Transferring => QueueSection.Now,
        QueueState.Waiting => QueueSection.UpNext,
        QueueState.Paused or QueueState.Cancelled => QueueSection.Stopped,
        _ => QueueSection.Done,
    };

    /// <summary>
    /// The album a file is downloaded for: its folder's name when that folder is directly inside the download folder
    /// (<paramref name="root"/>), as an album's download is; null for a file in the download folder itself. Folders are
    /// compared by name, as the server's are its own paths (and "~" for the user's home).
    /// </summary>
    public static string? AlbumOf(string folder, string root)
    {
        var parts = Segments(folder);
        var rootName = Segments(root).LastOrDefault();
        return parts.Length >= 2 && rootName is not null && string.Equals(parts[^2], rootName, StringComparison.OrdinalIgnoreCase)
               ? parts[^1]
               : null;
    }

    private static string[] Segments(string path) => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
