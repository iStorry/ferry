using System.Globalization;
using System.Text.Json;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Json;
using Ferry.Core.Queue;
using Ferry.Core.Sites;

namespace Ferry.Plugins.RemoteQueues;

/// <summary>
/// A Synology NAS's Download Station, over its web API. An account with two-step sign-in can't be used, so it needs one without.
/// It is only given an address: a file that needs a referer or a login won't download there.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "It is a queue of downloads, though not a Queue<T>.")]
public sealed class SynologyQueue(HttpFetcher http, Uri address, string account, string? password, string? folder, Func<SiteCatalog?> sites) : PolledQueue(address, sites)
{
    private const string Who = "The NAS";
    private const int SessionGone = 106;
    private const int SessionTimedOut = 105;
    private const int SessionTaken = 107;
    private const int SessionBlocked = 119;

    private static readonly TimeSpan AskLimit = TimeSpan.FromSeconds(20);

    private string? _session;

    public override string Name => "Download Station";

    protected override async Task<IReadOnlyList<RemoteTask>> ListAsync(CancellationToken cancel)
    {
        using var json = JsonAnswer.Parse(await AskTaskAsync("list", cancel, ("additional", "transfer,detail")), Who, Stage.Server);
        return json.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("tasks", out var tasks) ? [.. tasks.EnumerateArray().Select(Read)] : [];
    }

    protected override async Task<string> AddFileAsync(ResolvedFile file, RequestDetails details, AddOptions? options, CancellationToken cancel)
    {
        await AskTaskAsync("create", cancel, options?.Folder is { } chosen ? [("uri", file.Url.ToString()), ("destination", chosen)]
                                           : folder is { Length: > 0 } share ? [("uri", file.Url.ToString()), ("destination", share)]
                                           : [("uri", file.Url.ToString())]);
        // it doesn't say what it called the download: this one stands for it until the next reading replaces it
        return $"new-{Guid.NewGuid():N}";
    }

    protected override Task PauseTaskAsync(string task, CancellationToken cancel) => AskTaskAsync("pause", cancel, ("id", task));

    protected override Task ResumeTaskAsync(string task, CancellationToken cancel) => AskTaskAsync("resume", cancel, ("id", task));

    protected override Task StopTaskAsync(string task, CancellationToken cancel) => AskTaskAsync("delete", cancel, ("id", task), ("force_complete", "false"));

    protected override Task ForgetTaskAsync(string task, CancellationToken cancel) => StopTaskAsync(task, cancel);

    internal static RemoteTask Read(JsonElement task)
    {
        var status = task.TryGetProperty("status", out var said) ? said.GetString() ?? "" : "";
        var state = status switch
        {
            "downloading" or "finishing" or "hash_checking" or "extracting" => QueueState.Downloading,
            "waiting" or "filehosting_waiting" => QueueState.Waiting,
            "paused" => QueueState.Paused,
            "finished" or "seeding" => QueueState.Done,
            "error" => QueueState.Failed,
            _ => QueueState.Waiting,
        };
        var extra = task.TryGetProperty("additional", out var more) ? more : default;
        var transfer = extra.ValueKind == JsonValueKind.Object && extra.TryGetProperty("transfer", out var moved) ? moved : default;
        var detail = extra.ValueKind == JsonValueKind.Object && extra.TryGetProperty("detail", out var about) ? about : default;
        var size = Number(task, "size");
        var uri = detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("uri", out var given) && Uri.TryCreate(given.GetString(), UriKind.Absolute, out var link) ? link : null;
        var where = detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("destination", out var destination) ? destination.GetString() ?? "" : "";
        return new RemoteTask(task.GetProperty("id").GetString() ?? "", state, null, Number(transfer, "size_downloaded"), size > 0 ? size : null, Number(transfer, "speed_download"), 0,
                              state == QueueState.Failed ? $"Download Station: {(task.TryGetProperty("status_extra", out var why) ? why.ToString() : status)}" : null, uri, where);
    }

    private static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    /// <summary>Ask about downloads, signing in first if that is needed, and once more if the session has ended.</summary>
    /// <exception cref="StageException">it couldn't be reached, or refused</exception>
    private async Task<string> AskTaskAsync(string method, CancellationToken cancel, params (string Name, string Value)[] parameters)
    {
        for (var tries = 0; ; tries++)
        {
            _session ??= await SignInAsync(cancel);
            var answer = await PostAsync("webapi/DownloadStation/task.cgi",
                [("api", "SYNO.DownloadStation.Task"), ("version", "1"), ("method", method), ("_sid", _session), .. parameters], cancel);
            var code = ErrorOf(answer);
            if (code is null)
                return answer;
            if (code is SessionGone or SessionTimedOut or SessionTaken or SessionBlocked && tries == 0)
            {
                _session = null;
                continue;
            }
            throw new StageException(Stage.Server, $"Download Station refused with error {code.Value.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private async Task<string> SignInAsync(CancellationToken cancel)
    {
        var answer = await PostAsync("webapi/auth.cgi",
            [("api", "SYNO.API.Auth"), ("version", "3"), ("method", "login"), ("account", account), ("passwd", password ?? ""), ("session", "DownloadStation"), ("format", "sid")], cancel);
        if (ErrorOf(answer) is { } code)
            throw new StageException(Stage.Server, $"The NAS refused the sign-in for {account} (error {code.ToString(CultureInfo.InvariantCulture)}). An account with two-step sign-in can't be used.");
        using var json = JsonAnswer.Parse(answer, Who, Stage.Server);
        return json.RootElement.GetProperty("data").GetProperty("sid").GetString() ?? throw new StageException(Stage.Server, "The NAS gave no session.");
    }

    /// <summary>The error in an answer; null when it succeeded.</summary>
    private static int? ErrorOf(string answer)
    {
        using var json = JsonAnswer.Parse(answer, Who, Stage.Server);
        try
        {
            var root = json.RootElement;
            if (root.TryGetProperty("success", out var success) && success.GetBoolean())
                return null;
            return root.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code) ? code.GetInt32() : 0;
        }
        catch (InvalidOperationException e)
        {
            throw new StageException(Stage.Server, $"{Who}'s answer could not be read.", inner: e);
        }
    }

    private async Task<string> PostAsync(string path, IEnumerable<(string Name, string Value)> form, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(AskLimit);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Address, path))
        {
            Content = new FormUrlEncodedContent(form.Select(field => new KeyValuePair<string, string>(field.Name, field.Value))),
        };
        try
        {
            using var response = await http.SendAsync(request, Stage.Server, limit.Token);
            return await HttpFetcher.ReadTextAsync(response, HttpAnswers.MaxApiBytes, Stage.Server, limit.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new StageException(Stage.Server, $"The NAS at {Address.Host} didn't answer.", isRetryable: true);
        }
    }
}
