using System.Text.Json;
using Ferry.Core.Json;
using Ferry.Core.Queue;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>`ferry agent read`: a part of a finished file, and nothing else the PC might ask for.</summary>
public sealed class AgentReadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ferry_read_").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AgentPaths Agent => new(Path.Combine(_root, "agent"));

    private string Downloads => Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;

    /// <summary>The agent's queue.json with one item, and the file it names.</summary>
    private (Guid Id, string File, byte[] Data) Queue(QueueState state = QueueState.Done, string? path = null, bool write = true)
    {
        var data = new byte[3000];
        new Random(7).NextBytes(data);
        var file = path ?? Path.Combine(Downloads, "clip.mp4");
        if (write)
            File.WriteAllBytes(file, data);
        var id = Guid.NewGuid();
        List<SavedQueueItem> items = [new(id, "https://example.com/f/clip", Downloads, null, state, file, data.Length, data.Length, 0, null, null)];
        Directory.CreateDirectory(Agent.Root);
        File.WriteAllText(Agent.QueueFile, JsonSerializer.Serialize(items, FerryFileJson.Default.ListSavedQueueItem));
        return (id, file, data);
    }

    [Fact]
    public async Task APartOfAFinishedFileComesOut()
    {
        var (id, file, data) = Queue();
        using var output = new MemoryStream();

        Assert.Equal(Path.GetFullPath(file), AgentRead.FinishedFile(Agent, id));
        await AgentRead.CopyAsync(AgentRead.FinishedFile(Agent, id), 1000, 1500, output, CancellationToken.None);

        Assert.Equal(data[1000..2500], output.ToArray());
    }

    [Fact]
    public async Task ThePartMustBeInsideTheFile()
    {
        var (id, _, _) = Queue();
        var file = AgentRead.FinishedFile(Agent, id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentRead.CopyAsync(file, 2000, 1001, Stream.Null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentRead.CopyAsync(file, -1, 10, Stream.Null, CancellationToken.None));
        await AgentRead.CopyAsync(file, 3000, 0, Stream.Null, CancellationToken.None); // nothing left is still a part
    }

    [Fact]
    public void OnlyAFinishedFileOfTheQueueIsRead()
    {
        Assert.Contains("not in the server's queue", Refused(Guid.NewGuid()), StringComparison.Ordinal);

        var (downloading, _, _) = Queue(QueueState.Downloading);
        Assert.Contains("not finished", Refused(downloading), StringComparison.Ordinal);

        var (gone, file, _) = Queue();
        File.Delete(file);
        Assert.Contains("no longer on the server", Refused(gone), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOutsideItsFolderIsNotRead()
    {
        var elsewhere = Path.Combine(_root, "secret.txt");
        var (id, _, _) = Queue(path: elsewhere);

        Assert.Contains("not in the server's download folder", Refused(id), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileSortedIntoAFolderUnderItsOwnIsRead()
    {
        var sorted = Directory.CreateDirectory(Path.Combine(Downloads, "Videos")).FullName;
        var (id, file, _) = Queue(path: Path.Combine(sorted, "clip.mp4"));

        Assert.Equal(Path.GetFullPath(file), AgentRead.FinishedFile(Agent, id));
    }

    [Fact]
    public void AFileCantLeaveItsFolderByClimbingOutOrByAFolderOfALikeName()
    {
        var (climbing, _, _) = Queue(path: Path.Combine(Downloads, "..", "secret.txt"));
        Assert.Contains("not in the server's download folder", Refused(climbing), StringComparison.Ordinal);

        var alike = Directory.CreateDirectory(Downloads + "-old").FullName;
        var (beside, _, _) = Queue(path: Path.Combine(alike, "secret.txt"));
        Assert.Contains("not in the server's download folder", Refused(beside), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableQueueIsLeftWhereItIs()
    {
        Directory.CreateDirectory(Agent.Root);
        File.WriteAllText(Agent.QueueFile, "{ not json");

        Assert.Contains("can't be read", Refused(Guid.NewGuid()), StringComparison.Ordinal);
        Assert.Equal("{ not json", File.ReadAllText(Agent.QueueFile)); // the agent's own file, untouched
    }

    private string Refused(Guid id) => Assert.Throws<InvalidOperationException>(() => AgentRead.FinishedFile(Agent, id)).Message;
}
