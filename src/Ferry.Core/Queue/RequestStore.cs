using System.Text.Json;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Json;

namespace Ferry.Core.Queue;

/// <summary>
/// The request details of the files in the queue, kept apart from queue.json in the <see cref="SecretStore"/>: they can
/// be logins, and an unfinished file needs them again after a restart.
/// </summary>
/// <param name="log">where it says that what was saved can't be read; nowhere when null</param>
public sealed class RequestStore(SecretStore secrets, Action<string, Exception>? log = null)
{
    private const string SecretName = "queue-requests";
    private byte[]? _written;

    /// <summary>
    /// What was saved, by queue item; none if nothing was. When it can't be read the queue still comes back: those files
    /// ask their servers without the details, and one that needs them fails and says so on its row.
    /// </summary>
    public Dictionary<Guid, RequestDetails> Load()
    {
        try
        {
            if (secrets.Load(SecretName) is not { } bytes)
                return [];
            _written = bytes;
            return JsonSerializer.Deserialize(bytes, FerryFileJson.Default.SavedRequests) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke("reading the queue's request details", e);
            return [];
        }
    }

    /// <exception cref="IOException">the secret can't be written</exception>
    public void Save(Dictionary<Guid, RequestDetails> requests)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(requests, FerryFileJson.Default.SavedRequests);
        if (_written is not null && bytes.AsSpan().SequenceEqual(_written))
            return;
        if (requests.Count == 0)
            secrets.Delete(SecretName);
        else
            secrets.Save(SecretName, bytes);
        _written = bytes;
    }
}
