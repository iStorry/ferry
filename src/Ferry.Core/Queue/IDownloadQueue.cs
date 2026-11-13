using System.Diagnostics.CodeAnalysis;

namespace Ferry.Core.Queue;

/// <summary>
/// The download queue as the front ends see it, whatever does the downloading.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "It is a queue of downloads, though not a Queue<T>.")]
public interface IDownloadQueue : IAsyncDisposable
{
    /// <summary>Raised on a background thread, after the change; handlers may call back into the queue. Events can
    /// arrive out of order across threads: an item with a lower <see cref="QueueItem.Revision"/> than one already
    /// seen is older news.</summary>
    event Action<QueueEvent>? Changed;

    IReadOnlyList<QueueItem> Items { get; }

    /// <summary>Completes when nothing is waiting or downloading.</summary>
    Task WhenIdle { get; }

    QueueItem? Find(Guid id);

    /// <summary>
    /// Queue each line of <paramref name="lines"/> that is a file link; blank lines and lines starting with "#" are
    /// passed over.
    /// </summary>
    Task<QueueAddResult> AddAsync(IEnumerable<string> lines, AddOptions? options = null);

    /// <summary>Stop the file, keeping what it downloaded; returns once it has stopped.</summary>
    Task PauseAsync(Guid id);

    /// <summary>Put a paused file back in line.</summary>
    Task ResumeAsync(Guid id);

    /// <summary>Stop the file for good unless it is retried, keeping what it downloaded; returns once it has stopped.</summary>
    Task CancelAsync(Guid id);

    /// <summary>Put a failed or cancelled file back in line; it continues from what it downloaded.</summary>
    Task RetryAsync(Guid id);

    /// <summary>Take the file out of the queue, stopping it first.</summary>
    /// <param name="deleteParts">also delete what an unfinished download wrote; a finished file is never deleted</param>
    Task RemoveAsync(Guid id, bool deleteParts);

    /// <summary>Save the file under <paramref name="name"/> instead of the server's name; only before it has started.</summary>
    /// <exception cref="InvalidOperationException">the file has already started under its name</exception>
    Task RenameAsync(Guid id, string? name);

    /// <summary>Limit this file's speed, also while it downloads; 0 removes the limit.</summary>
    Task SetSpeedLimitAsync(Guid id, long bytesPerSecond);

    Task PauseAllAsync();

    Task ResumeAllAsync();
}
