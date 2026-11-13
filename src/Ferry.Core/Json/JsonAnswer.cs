using System.Text.Json;

namespace Ferry.Core.Json;

/// <summary>A JSON answer from a site's API, read as a document.</summary>
public static class JsonAnswer
{
    /// <summary>Parse <paramref name="text"/>; the caller disposes the document.</summary>
    /// <param name="who">whose answer it is, for the message when it isn't JSON</param>
    /// <exception cref="StageException">it isn't JSON</exception>
    public static JsonDocument Parse(string text, string who, Stage stage = Stage.Resolve)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            throw new StageException(stage, $"{who}'s answer could not be read.", inner: e);
        }
    }
}
