using System.Globalization;
using System.Text;
using System.Text.Json;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Json;
using Ferry.Core.Queue;
using Ferry.Core.Sites;

namespace Ferry.Plugins.RemoteQueues;

/// <summary>An aria2 daemon's queue, over its JSON-RPC.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "It is a queue of downloads, though not a Queue<T>.")]
public sealed class Aria2Queue(HttpFetcher http, Uri address, string? token, Func<SiteCatalog?> sites) : PolledQueue(address, sites)
{
    private const int MostListed = 1000;
    private static readonly TimeSpan AskLimit = TimeSpan.FromSeconds(15);

    private int _requestId;

    public override string Name => "aria2";

    public override Task PauseAllAsync() => AskAsync("aria2.forcePauseAll", Stopping);

    public override Task ResumeAllAsync() => AskAsync("aria2.unpauseAll", Stopping);

    protected override async Task<IReadOnlyList<RemoteTask>> ListAsync(CancellationToken cancel)
    {
        var tasks = new List<RemoteTask>();
        foreach (var answer in new[] { await AskAsync("aria2.tellActive", cancel), await AskAsync("aria2.tellWaiting", cancel, 0, MostListed), await AskAsync("aria2.tellStopped", cancel, 0, MostListed) })
        {
            using var json = JsonAnswer.Parse(answer, Name, Stage.Server);
            try
            {
                tasks.AddRange(json.RootElement.GetProperty("result").EnumerateArray().Select(Read));
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
            {
                throw new StageException(Stage.Server, $"{Name}'s answer could not be read.", inner: e);
            }
        }
        return tasks;
    }

    protected override async Task<string> AddFileAsync(ResolvedFile file, RequestDetails details, AddOptions? options, CancellationToken cancel)
    {
        var choices = new Dictionary<string, string> { ["user-agent"] = file.UserAgent, ["out"] = options?.Name ?? file.Name };
        if (file.Referer is { } referer)
            choices["referer"] = referer.ToString();
        if (options?.Folder is { } folder)
            choices["dir"] = folder;
        if (options is { Paused: true })
            choices["pause"] = "true";
        // a login is for the link's own site, not a CDN a site's link resolved to
        if (HttpFetcher.IsSameSite(file.Url, file.Link.Original) && details.Cookies is { Length: > 0 } cookies)
            choices["header"] = $"Cookie: {cookies}";
        using var json = JsonAnswer.Parse(await AskAsync("aria2.addUri", cancel, new[] { file.Url.ToString() }, choices), Name, Stage.Server);
        return json.RootElement.GetProperty("result").GetString() ?? throw new StageException(Stage.Server, "aria2 didn't say what it called the download.");
    }

    protected override Task PauseTaskAsync(string task, CancellationToken cancel) => AskAsync("aria2.forcePause", cancel, task);

    protected override Task ResumeTaskAsync(string task, CancellationToken cancel) => AskAsync("aria2.unpause", cancel, task);

    protected override Task StopTaskAsync(string task, CancellationToken cancel) => AskAsync("aria2.forceRemove", cancel, task);

    protected override Task ForgetTaskAsync(string task, CancellationToken cancel) => AskAsync("aria2.removeDownloadResult", cancel, task);

    protected override Task LimitTaskAsync(string task, long bytesPerSecond, CancellationToken cancel) =>
        AskAsync("aria2.changeOption", cancel, task, new Dictionary<string, string> { ["max-download-limit"] = bytesPerSecond.ToString(CultureInfo.InvariantCulture) });

    internal static RemoteTask Read(JsonElement entry)
    {
        var state = (entry.GetProperty("status").GetString() ?? "") switch
        {
            "active" => QueueState.Downloading,
            "waiting" => QueueState.Waiting,
            "paused" => QueueState.Paused,
            "complete" => QueueState.Done,
            "error" => QueueState.Failed,
            _ => QueueState.Cancelled,
        };
        var file = entry.TryGetProperty("files", out var files) && files.GetArrayLength() > 0 ? files[0] : default;
        var path = file.ValueKind == JsonValueKind.Object && file.TryGetProperty("path", out var written) && written.GetString() is { Length: > 0 } text ? text : null;
        var total = Number(entry, "totalLength");
        return new RemoteTask(entry.GetProperty("gid").GetString() ?? "", state, path, Number(entry, "completedLength"), total > 0 ? total : null, Number(entry, "downloadSpeed"), (int)Number(entry, "connections"),
                              state == QueueState.Failed && entry.TryGetProperty("errorMessage", out var error) ? error.GetString() : null, FirstUri(file),
                              entry.TryGetProperty("dir", out var dir) ? dir.GetString() ?? "" : "");
    }

    private static Uri? FirstUri(JsonElement file) =>
        file.ValueKind == JsonValueKind.Object && file.TryGetProperty("uris", out var uris) && uris.GetArrayLength() > 0 && uris[0].TryGetProperty("uri", out var uri)
        && Uri.TryCreate(uri.GetString(), UriKind.Absolute, out var link) ? link : null;

    private static long Number(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && long.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var number) ? number : 0;

    /// <exception cref="StageException">aria2 couldn't be reached, or refused</exception>
    private async Task<string> AskAsync(string method, CancellationToken cancel, params object[] parameters)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(AskLimit);
        using var request = new HttpRequestMessage(HttpMethod.Post, Address) { Content = new StringContent(Body(method, parameters), Encoding.UTF8, "application/json") };
        try
        {
            using var response = await http.SendAsync(request, Stage.Server, limit.Token);
            return await HttpFetcher.ReadTextAsync(response, HttpAnswers.MaxApiBytes, Stage.Server, limit.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new StageException(Stage.Server, $"aria2 at {Address.Host} didn't answer.", isRetryable: true);
        }
    }

    private string Body(string method, object[] parameters)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WriteString("id", Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture));
            json.WriteString("method", method);
            json.WriteStartArray("params");
            if (token is not null)
                json.WriteStringValue($"token:{token}");
            foreach (var parameter in parameters)
                WriteValue(json, parameter);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter json, object value)
    {
        switch (value)
        {
            case string text:
                json.WriteStringValue(text);
                break;
            case int number:
                json.WriteNumberValue(number);
                break;
            case string[] list:
                json.WriteStartArray();
                foreach (var item in list)
                    json.WriteStringValue(item);
                json.WriteEndArray();
                break;
            case Dictionary<string, string> choices:
                json.WriteStartObject();
                foreach (var (name, choice) in choices)
                    json.WriteString(name, choice);
                json.WriteEndObject();
                break;
            default:
                throw new ArgumentException($"aria2 isn't asked with a {value.GetType().Name}.", nameof(value));
        }
    }
}
