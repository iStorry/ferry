using System.Text.Json.Serialization;
using Ferry.Core.Files;
using Ferry.Core.Json;

namespace Ferry.Core.Queue;

/// <summary>A queue item as saved in queue.json: the link as given, not a signed URL, which would have expired.</summary>
public sealed record SavedQueueItem(Guid Id, string Link, string Folder, string? Name, QueueState State, string? Path,
                                    long Done, long? Total, long SpeedLimit, string? Error, [property: JsonConverter(typeof(SavedStageConverter))] Stage? FailedAt, bool FolderChosen = false,
                                    string? Sha256 = null)
{
    public static SavedQueueItem From(QueueItem item) =>
        new(item.Id, item.Link.Original.ToString(), item.Folder, item.Name, item.State, item.Path, item.Done, item.Total,
            item.SpeedLimit, item.Error, item.FailedAt, item.FolderChosen, item.Sha256);
}

/// <summary>
/// The app's queue on disk (<see cref="Settings.AppPaths.QueueFile"/>), so unfinished files survive a restart. The
/// downloaded bytes themselves are in each file's part file; this only remembers which files there were.
/// </summary>
public sealed class QueueStore(string path)
{
    private readonly JsonListFile<SavedQueueItem> _file = new(path, FerryFileJson.Default.ListSavedQueueItem);

    public string FilePath => _file.FilePath;

    /// <summary>
    /// The saved items; none if there is no file. A file that can't be read is moved aside to "queue.json.unreadable"
    /// (so it is not overwritten), and the queue starts empty.
    /// </summary>
    /// <exception cref="IOException">the file is there but can't be opened</exception>
    public IReadOnlyList<SavedQueueItem> Load() => _file.Load();

    /// <summary>
    /// The saved items, for a process that only looks (another than the one saving): a file that can't be read stays
    /// where it is. Saves replace the file whole, so a read sees the old list or the new one.
    /// </summary>
    /// <exception cref="InvalidDataException">the file can't be read as a queue</exception>
    /// <exception cref="IOException">the file is there but can't be opened</exception>
    public IReadOnlyList<SavedQueueItem> Read() => _file.Read();

    /// <exception cref="IOException">the file can't be written</exception>
    public void Save(IEnumerable<QueueItem> items) => _file.Save([.. items.Select(SavedQueueItem.From)]);
}
