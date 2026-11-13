using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ferry.Core.Files;

/// <summary>
/// A list kept as a JSON file in the config folder. Saves replace the file whole (<see cref="AtomicFile"/>), so a
/// crash or another process reading meanwhile sees the old list or the new one, never half of one.
/// </summary>
public sealed class JsonListFile<T>(string path, JsonTypeInfo<List<T>> type)
{
    private const string UnreadableSuffix = ".unreadable";

    public string FilePath { get; } = path;

    /// <summary>
    /// The saved list; empty if there is no file. A file that can't be read is moved aside ("{name}.unreadable", so it
    /// is not overwritten) and the list starts empty.
    /// </summary>
    /// <exception cref="IOException">the file is there but can't be opened</exception>
    public List<T> Load()
    {
        try
        {
            return Read();
        }
        catch (InvalidDataException)
        {
            File.Move(FilePath, FilePath + UnreadableSuffix, overwrite: true);
            return [];
        }
    }

    /// <summary>The saved list, for a process that only looks: a file that can't be read stays where it is.</summary>
    /// <exception cref="InvalidDataException">the file isn't a list of <typeparamref name="T"/></exception>
    /// <exception cref="IOException">the file is there but can't be opened</exception>
    public List<T> Read()
    {
        if (!File.Exists(FilePath))
            return [];
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), type) ?? [];
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{FilePath} can't be read: {e.Message}", e);
        }
    }

    /// <exception cref="IOException">the file can't be written</exception>
    public void Save(List<T> items)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(FilePath)) is { } folder)
            Directory.CreateDirectory(folder);
        AtomicFile.WriteText(FilePath, JsonSerializer.Serialize(items, type));
    }
}
