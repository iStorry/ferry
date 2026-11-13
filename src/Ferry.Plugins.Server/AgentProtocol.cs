using Ferry.Core.Browse;
using Ferry.Core.Download;
using Ferry.Core.Library;
using Ferry.Core.Http;
using Ferry.Core.Sites;
using Ferry.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Ferry.Core.Queue;
using Ferry.Core.Settings;

namespace Ferry.Plugins.Server;

/// <summary>What the PC can ask the server's agent to do. There is no other way in: no shell, no paths.</summary>
public enum AgentCommand
{
    /// <summary>First, always: the PC's version and settings. Answered with the agent's version and its queue.</summary>
    Hello,

    /// <summary>Nothing: an answer says the agent is there (the heartbeat).</summary>
    Ping,

    /// <summary>The PC's settings changed (<see cref="AgentRequest.Settings"/>, all of them, as Hello sends them): use them from now on.</summary>
    Settings,
    Add,

    /// <summary>
    /// The request details (<see cref="AgentRequest.Details"/>) a file (<see cref="AgentRequest.Item"/>) was added with, again:
    /// the agent keeps them in memory only, so after it restarts the PC sends them once more.
    /// </summary>
    Details,

    /// <summary>
    /// Read a link to many files (<see cref="AgentRequest.Link"/>, with <see cref="AgentRequest.Details"/>) on the server, and
    /// answer with the files (<see cref="AgentMessage.Files"/>); without them when it is a link to one.
    /// </summary>
    Expand,

    /// <summary>
    /// Read what a link (<see cref="AgentRequest.Link"/>, with <see cref="AgentRequest.Details"/>) leads to on the server,
    /// and answer with the file's name (<see cref="AgentMessage.Name"/>) and size (<see cref="AgentMessage.Size"/>).
    /// </summary>
    Read,
    Pause,
    Resume,
    Cancel,
    Retry,
    Remove,
    Rename,
    SetLimit,
    PauseAll,
    ResumeAll,

    /// <summary>A finished file's path, size and SHA-256.</summary>
    FileInfo,

    /// <summary>The file was brought over: forget it, and delete it unless asked to keep it.</summary>
    Collected,

    /// <summary>Free space in the download folder.</summary>
    Space,

    /// <summary>How the agent and its server are doing: memory, CPU time, load, disk (<see cref="AgentStats"/>).</summary>
    Stats,

    /// <summary>The end of the agent's log (agent.log), at most 64 KB.</summary>
    Log,

    /// <summary>Read an album or file page (<see cref="AgentRequest.Link"/>) and say what is on it.</summary>
    Browse,

    /// <summary>Watch an album (<see cref="AgentRequest.Page"/>), every file on it seen.</summary>
    Watch,

    /// <summary>Stop watching an album (<see cref="AgentRequest.Link"/>).</summary>
    Unwatch,

    /// <summary>Every file on an album (<see cref="AgentRequest.Page"/>) has been seen.</summary>
    MarkSeen,

    /// <summary>Read every watched album now.</summary>
    CheckWatched,

    /// <summary>Read the albums the site lists for <see cref="AgentRequest.Query"/> (<see cref="AgentMessage.Cards"/>).</summary>
    Albums,

    /// <summary>Read an album's picture (<see cref="AgentRequest.Link"/>) on the list read last (<see cref="AgentMessage.Picture"/>).</summary>
    Thumbnail,

    /// <summary>Read how many files an album (<see cref="AgentRequest.Link"/>) has and how large it is (<see cref="AgentMessage.Details"/>).</summary>
    AlbumDetails,

    /// <summary>Search the site a plugin knows for a title (<see cref="AgentRequest.Name"/>), and answer with what it lists (<see cref="AgentMessage.Titles"/>).</summary>
    TitleSearch,
}

public enum AgentEventKind
{
    Added,
    StateChanged,
    Updated,
    Removed,

    /// <summary>The watched albums changed: <see cref="AgentMessage.Albums"/> has all of them.</summary>
    WatchedChanged,
}

/// <summary>One line from the PC to the agent. Which fields are used depends on <see cref="Command"/>.</summary>
/// <param name="Flag">Remove: delete parts. Collected: keep the file on the server.</param>
/// <param name="Settings">Hello, Settings: the PC's settings that aren't defaults</param>
/// <param name="Link">Browse, Unwatch, Thumbnail, AlbumDetails: the album's, file's or picture's link. Expand, Read: the link to read</param>
/// <param name="Secrets">Hello, Settings: the secrets of the plugins installed on the server, by name; kept in the agent's memory only</param>
/// <param name="Page">Watch, MarkSeen: the album as the PC has it</param>
/// <param name="Query">Albums: which albums to list</param>
/// <param name="Folder">Add: a folder of their own for the files, under the download folder (an album's name)</param>
/// <param name="Details">Add, Details, Expand, Read: what to send with every request for these files; kept in the agent's memory only</param>
/// <param name="Sha256">Add: what the finished file must hash to</param>
internal sealed record AgentRequest(int Id, AgentCommand Command, Guid? Item = null, List<string>? Lines = null,
                                    string? Name = null, long? BytesPerSecond = null, bool? Flag = null,
                                    string? Version = null, Dictionary<string, string>? Settings = null,
                                    string? Link = null, string? Folder = null, RequestDetails? Details = null, string? Sha256 = null,
                                    BrowsedPage? Page = null, AlbumQuery? Query = null, Dictionary<string, string>? Secrets = null);

/// <summary>
/// One line from the agent to the PC: the answer to request <see cref="Answers"/>, or an event
/// (<see cref="Event"/>) about a queue item.
/// </summary>
/// <param name="Error">the request failed; the message is for the user</param>
/// <param name="Items">Hello: the whole queue. Add: the items added.</param>
/// <param name="Plugins">Hello: the plugins installed on the server, loaded or not</param>
/// <param name="Page">Browse: what is on the page</param>
/// <param name="Albums">Hello, and the WatchedChanged event: every watched album</param>
/// <param name="Cards">Albums: the albums the site lists</param>
/// <param name="Picture">Thumbnail: the image's bytes (base64 on the wire)</param>
/// <param name="Details">AlbumDetails: the album's files and size</param>
/// <param name="Titles">TitleSearch: what the site lists for the title, newest first</param>
/// <param name="Folder">FileInfo: the folder of its own the file is in, under the download folder, or null</param>
/// <param name="Files">Expand: the files behind the link; null when it is a link to one file</param>
/// <param name="Name">Read: the file's name as its site gives it</param>
/// <param name="Size">FileInfo: the finished file's size. Read: the file's size, when its site says it before downloading</param>
internal sealed record AgentMessage(int? Answers = null, string? Error = null, Stage? ErrorStage = null,
                                    string? Version = null, List<RemoteItem>? Items = null,
                                    List<RemoteItem>? Duplicates = null, List<RejectedLink>? Rejected = null,
                                    string? Path = null, long? Size = null, string? Sha256 = null, long? FreeBytes = null,
                                    AgentEventKind? Event = null, RemoteItem? Item = null, QueueState? Was = null,
                                    AgentStats? Stats = null, string? Log = null, string? Folder = null, FileList? Files = null,
                                    string? Name = null, List<AgentPlugin>? Plugins = null, BrowsedPage? Page = null,
                                    List<WatchedAlbum>? Albums = null, List<AlbumCard>? Cards = null, byte[]? Picture = null,
                                    AlbumDetails? Details = null, List<FoundTitle>? Titles = null);

/// <summary>The one plugin whose site the agent browses, for the Browser page: its pages, its list of albums, and albums watched.</summary>
/// <param name="Name">what people call the plugin</param>
/// <param name="IsOn">whether it is on, as the settings are</param>
/// <param name="TryReadPage">a link of its own as the page it is, as the settings are; false when it is no page of this site</param>
/// <param name="Make">what reads the site, with the settings and secrets as they are</param>
public sealed record AgentBrowsing(string Name, Func<FerrySettings, bool> IsOn, Func<string, FerrySettings, PageLink?> TryReadPage,
                                   Func<PluginContext, Ferry.Core.Plugins.ISiteBrowsing> Make);

/// <summary>The one plugin whose site the agent searches for titles, for the Library page.</summary>
/// <param name="Name">what people call the plugin</param>
/// <param name="IsOn">whether it is on, as the settings are</param>
/// <param name="Make">what searches the site, with the settings and secrets as they are</param>
public sealed record AgentLookup(string Name, Func<FerrySettings, bool> IsOn, Func<PluginContext, Ferry.Core.Plugins.ITitleLookup> Make);

/// <summary>A plugin installed on the server, as the agent found it when it started.</summary>
/// <param name="Sha256">of its package, so the PC can tell whether the server has the build it has</param>
/// <param name="Problem">why it isn't running, or null when it is</param>
public sealed record AgentPlugin(string Id, string Version, string Sha256, string? Problem);

/// <summary>A queue item on the wire.</summary>
/// <param name="Sha256">what the finished file hashes to, when the agent knows it (<see cref="QueueItem.Sha256"/>)</param>
internal sealed record RemoteItem(Guid Id, string SiteId, string Link, string Key, string Folder, string? Name, QueueState State,
                                  string? Path, long Done, long? Total, double BytesPerSecond, int Connections, int Retries,
                                  long SpeedLimit, string? Error, Stage? FailedAt, long Revision, string? Sha256 = null)
{
    public static RemoteItem From(QueueItem item) =>
        new(item.Id, item.Link.SiteId, item.Link.Original.ToString(), item.Link.Key, item.Folder, item.Name, item.State, item.Path, item.Done,
            item.Total, double.IsFinite(item.BytesPerSecond) ? item.BytesPerSecond : 0, item.Connections, item.Retries,
            item.SpeedLimit, item.Error, item.FailedAt, item.Revision, item.Sha256);

    public QueueItem ToItem() =>
        new(Id, new FileLink(SiteId, new Uri(Link), Key), Folder, Name, State, Path, Done, Total, BytesPerSecond, Connections, Retries,
            SpeedLimit, Error, FailedAt, Revision, Sha256: Sha256);
}

/// <summary>The agent's version differs from the PC's; the agent must be replaced before they can talk.</summary>
public sealed class AgentVersionException(string pcVersion, string agentVersion)
    : Exception($"The server runs Ferry agent {agentVersion}, but this is Ferry {pcVersion}.")
{
    public string PcVersion { get; } = pcVersion;
    public string AgentVersion { get; } = agentVersion;
}

/// <summary>JSON lines over a pair of streams, one message per line, for both ends of the connection.</summary>
internal sealed class AgentLines : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writing = new(1, 1);

    public AgentLines(Stream input, Stream output)
    {
        _reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        _writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: ReferenceEquals(input, output)) { NewLine = "\n" };
    }

    /// <summary>The next line, or null once the other end has closed.</summary>
    public ValueTask<string?> ReadAsync(CancellationToken cancel) => _reader.ReadLineAsync(cancel);

    public async Task WriteAsync<T>(T message, JsonTypeInfo<T> type, CancellationToken cancel)
    {
        var line = JsonSerializer.Serialize(message, type);
        await _writing.WaitAsync(cancel);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancel);
            await _writer.FlushAsync(cancel);
        }
        finally
        {
            _writing.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _writer.DisposeAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // the other end is already gone, or a write into a connection gone quiet is still in flight: closing the stream ends it
        }
        finally
        {
            try
            {
                _reader.Dispose();
            }
            finally
            {
                _writing.Dispose();
            }
        }
    }
}

/// <summary>Source-generated JSON for the agent protocol (AOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentMessage))]
[JsonSerializable(typeof(AgentStats))]
internal sealed partial class AgentJson : JsonSerializerContext;
