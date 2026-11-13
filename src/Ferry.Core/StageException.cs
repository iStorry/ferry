using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ferry.Core;

/// <summary>The step of getting a file that went wrong.</summary>
public enum Stage
{
    Link,

    /// <summary>Finding out what the link leads to, and where its bytes are.</summary>
    Resolve,

    Download,

    /// <summary>Server Support: reaching the server, logging in, or talking to its agent.</summary>
    Server,

    /// <summary>Server Support: bringing a finished file over from the server.</summary>
    Transfer,

    /// <summary>Checking the finished file against the checksum it should have.</summary>
    Verify,

    /// <summary>Checking for, or fetching, a new version of Ferry from GitHub.</summary>
    Update,
}

/// <summary>
/// A saved <see cref="Stage"/>, by name. 1.1.0 had Page, Api and Sign where there is now <see cref="Stage.Resolve"/>: a queue
/// saved then still has to load, or the whole file would be moved aside for one failed file's sake.
/// </summary>
public sealed class SavedStageConverter : JsonConverter<Stage?>
{
    public override Stage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        null => null,
        "Page" or "Api" or "Sign" => Stage.Resolve,
        var name => Enum.TryParse<Stage>(name, out var stage) ? stage : throw new JsonException($"\"{name}\" is not a step."),
    };

    public override void Write(Utf8JsonWriter writer, Stage? value, JsonSerializerOptions options)
    {
        if (value is { } stage)
            writer.WriteStringValue(stage.ToString());
        else
            writer.WriteNullValue();
    }
}

/// <summary>Getting a file failed at <see cref="Stage"/>; the message is for the user.</summary>
/// <param name="isRetryable">trying the same step again may work (a timeout, 429, 5xx, a dropped connection)</param>
/// <param name="retryAfter">how long the server asked to wait before trying again</param>
/// <param name="status">the HTTP status the server answered with, when it answered with an error</param>
public sealed class StageException(Stage stage, string message, bool isRetryable = false, TimeSpan? retryAfter = null,
                                   Exception? inner = null, HttpStatusCode? status = null) : Exception(message, inner)
{
    public Stage Stage { get; } = stage;
    public bool IsRetryable { get; } = isRetryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public HttpStatusCode? Status { get; } = status;

    /// <summary>The server is overloaded or limiting how often it is asked (503, 429): fewer requests, more slowly.</summary>
    public bool IsBusy => Status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
}
