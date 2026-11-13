using Ferry.Core.Queue;

namespace Ferry.Plugins.Server;

/// <summary>
/// <c>ferry agent read &lt;item&gt; &lt;offset&gt; &lt;length&gt;</c>, run on the server over ssh, one per part of a file
/// coming over (docs/openssh-plan.md §5): that part of a finished file, on stdout. Only an item the agent's queue
/// has as done, and only its own file, somewhere under its own folder: the PC never names a path. It runs as a process of its
/// own beside the agent, so it reads the queue from the agent's queue.json.
/// </summary>
public static class AgentRead
{
    private const int BufferSize = 1 << 20;

    /// <summary>The finished file of the item <paramref name="id"/> in the agent's queue.</summary>
    /// <exception cref="InvalidOperationException">no such item, it isn't finished, or its file isn't in its folder or
    /// isn't there any more; the message says which</exception>
    public static string FinishedFile(AgentPaths paths, Guid id)
    {
        SavedQueueItem? item;
        try
        {
            item = new QueueStore(paths.QueueFile).Read().FirstOrDefault(i => i.Id == id);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The server's queue can't be read: {e.Message}", e);
        }
        if (item is null)
            throw new InvalidOperationException("That file is not in the server's queue.");
        if (item.State != QueueState.Done || item.Path is null)
            throw new InvalidOperationException("That file has not finished downloading on the server.");
        // an earlier agent sorted files into folders of their own under it; the full paths have no ".." left to climb out by
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Folder)) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(item.Path);
        if (!file.StartsWith(folder, StringComparison.Ordinal))
            throw new InvalidOperationException("That file is not in the server's download folder.");
        if (!File.Exists(file))
            throw new InvalidOperationException($"The finished file is no longer on the server ({file}).");
        return file;
    }

    /// <summary>Copy <paramref name="length"/> bytes of <paramref name="file"/> from <paramref name="offset"/> to
    /// <paramref name="output"/>.</summary>
    /// <exception cref="InvalidOperationException">the part isn't inside the file</exception>
    /// <exception cref="IOException">the file can't be read, or the PC stopped reading (it took the rest elsewhere)</exception>
    public static async Task CopyAsync(string file, long offset, long length, Stream output, CancellationToken cancel)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                                               FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset < 0 || length < 0 || offset > input.Length - length)
            throw new InvalidOperationException($"Bytes {offset}–{offset + length} are not inside the file ({input.Length} bytes).");
        input.Position = offset;
        var buffer = new byte[BufferSize];
        for (var left = length; left > 0;)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancel);
            if (read == 0)
                throw new IOException("The file got shorter while it was read.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            left -= read;
        }
        await output.FlushAsync(cancel);
    }
}
