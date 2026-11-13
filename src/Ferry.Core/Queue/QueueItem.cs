using Ferry.Core.Http;
using Ferry.Core.Sites;
using Ferry.Core.Text;

namespace Ferry.Core.Queue;

public enum QueueState
{
    /// <summary>In line for a free slot.</summary>
    Waiting,

    /// <summary>Finding out what the link leads to.</summary>
    Resolving,

    Downloading,

    /// <summary>Server Support: downloaded on the server, waiting to be brought over.</summary>
    OnServer,

    /// <summary>Server Support: being brought over from the server.</summary>
    Transferring,

    /// <summary>Stopped with what was downloaded kept; "Resume" puts it back in line.</summary>
    Paused,

    /// <summary>Stopped by the user with what was downloaded kept; "Retry" puts it back in line.</summary>
    Cancelled,

    Done,
    Failed,

    /// <summary>The file was already there and skipping such files was chosen.</summary>
    Skipped,
}

/// <summary>A file in the queue as it is at one moment.</summary>
/// <param name="Name">the name the user chose, or null for the server's</param>
/// <param name="Path">the file being written (or written, or skipped), once known</param>
/// <param name="Connections">connections receiving data right now</param>
/// <param name="Retries">connections that failed and were tried again</param>
/// <param name="SpeedLimit">this file's own limit in bytes per second, 0 for none</param>
/// <param name="FailedAt">the step that failed, when <see cref="State"/> is <see cref="QueueState.Failed"/></param>
/// <param name="Revision">counts the item's changes: of two snapshots of one item, the higher is the newer</param>
/// <param name="FolderChosen">the user chose <paramref name="Folder"/>, so the file isn't sorted by type</param>
/// <param name="Sha256">what the finished file must hash to; once it is done, what it hashes to, when that is known</param>
public sealed record QueueItem(Guid Id, FileLink Link, string Folder, string? Name, QueueState State, string? Path,
                               long Done, long? Total, double BytesPerSecond, int Connections, int Retries, long SpeedLimit,
                               string? Error, Stage? FailedAt, long Revision = 0, bool FolderChosen = false, string? Sha256 = null)
{
    public bool IsActive => State is QueueState.Resolving or QueueState.Downloading or QueueState.Transferring;

    /// <summary>What to call the file: its name on disk once known, else the chosen name, else the end of the link.</summary>
    public string DisplayName => System.IO.Path.GetFileName(Path) is { Length: > 0 } file ? file : Name ?? Link.Label;

    /// <summary>At the current speed; null when the size or the speed is unknown.</summary>
    public TimeSpan? TimeLeft => Total is { } total && State is QueueState.Downloading or QueueState.Transferring
        ? Units.TimeLeft(Math.Max(0, total - Done), BytesPerSecond)
        : null;
}

/// <summary>Something that happened in the queue, raised on a background thread.</summary>
public abstract record QueueEvent(QueueItem Item);

public sealed record QueueItemAdded(QueueItem Item) : QueueEvent(Item);

/// <param name="Was">the state before</param>
public sealed record QueueItemStateChanged(QueueItem Item, QueueState Was) : QueueEvent(Item);

/// <summary>Progress, speed, connections, retries or the name changed; the state did not.</summary>
public sealed record QueueItemUpdated(QueueItem Item) : QueueEvent(Item);

public sealed record QueueItemRemoved(QueueItem Item) : QueueEvent(Item);

/// <summary>A line that was not added, and why.</summary>
public sealed record RejectedLink(string Text, string Reason);

/// <summary>How links are to be added.</summary>
/// <param name="Name">save under this name instead of the server's (meant for a single link)</param>
/// <param name="Folder">the folder for these files, instead of the queue's</param>
/// <param name="Details">what to send with every request for these files</param>
/// <param name="Paused">they wait, paused, instead of getting in line</param>
/// <param name="Sha256">what the finished file must hash to, as <see cref="Files.FileChecksum.TryRead"/> gives it (meant for a single link)</param>
/// <param name="Album">a folder of their own under the queue's, an album's name, on whichever machine downloads</param>
public sealed record AddOptions(string? Name = null, string? Folder = null, RequestDetails? Details = null, bool Paused = false, string? Sha256 = null,
                                string? Album = null);

/// <param name="Duplicates">items already in the queue for links that were given again</param>
public sealed record QueueAddResult(IReadOnlyList<QueueItem> Added, IReadOnlyList<QueueItem> Duplicates,
                                    IReadOnlyList<RejectedLink> Rejected);
