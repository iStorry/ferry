using System.Collections;
using System.Text.Json;
using Ferry.Core.Json;

namespace Ferry.Core.Settings;

/// <summary>A setting could not be changed; the message is for the user.</summary>
public sealed class SettingsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The settings file: a JSON object of "FERRY_..." keys and text values, plus the app's own state under other keys.
/// It is per user (<see cref="AppPaths.SettingsFile"/>), or wherever FERRY_CONFIG_PATH points. Every change is
/// written to a temporary file first and then renamed over the old one, so a crash never leaves half a file.
/// </summary>
/// <param name="environment">environment variables; the process's own when null</param>
public sealed class SettingsStore(AppPaths paths, IReadOnlyDictionary<string, string>? environment = null)
{
    /// <summary>Points at another settings file; read from the environment only.</summary>
    public const string PathVariable = "FERRY_CONFIG_PATH";

    /// <summary>What a settings file is written as before it replaces the old one.</summary>
    internal const string TempSuffix = ".tmp";

    private readonly IReadOnlyDictionary<string, string> _environment = environment ?? ProcessEnvironment();

    /// <summary>The file in use: FERRY_CONFIG_PATH when set, else the one in <see cref="AppPaths.ConfigFolder"/>.</summary>
    public string FilePath => _environment.TryGetValue(PathVariable, out var path) && path.Length > 0
        ? Path.GetFullPath(path)
        : paths.SettingsFile;

    /// <summary>The settings in effect. A file that can't be read is reported in the problems, and the defaults used.</summary>
    public FerrySettings Load()
    {
        var problems = new List<SettingProblem>();
        Dictionary<string, string> file;
        try
        {
            file = ReadFile();
        }
        catch (SettingsException e)
        {
            problems.Add(new SettingProblem(FilePath, "", SettingSource.File, $"could not be read ({e.Message}), so it is ignored"));
            file = [];
        }
        return FerrySettings.Build(paths, file, _environment, problems);
    }

    /// <summary>Save <paramref name="value"/> for the setting <paramref name="name"/> (any spelling).</summary>
    /// <exception cref="SettingsException">not a setting, a value it can't take, or the file can't be read or written</exception>
    public FerrySettings Set(string name, string value)
    {
        var key = Find(name);
        if (!key.TryNormalise(value, out var normalised, out var reason))
            throw new SettingsException($"{key.Name}: \"{value}\" {reason}.");
        return Change(file => file[key.Name] = normalised);
    }

    /// <summary>Remove the setting from the file, so its default applies.</summary>
    /// <exception cref="SettingsException">not a setting, or the file can't be read or written</exception>
    public FerrySettings Unset(string name)
    {
        var key = Find(name);
        return Change(file => file.Remove(key.Name));
    }

    /// <summary>Remember something for the app (last page, skipped version); null forgets it.</summary>
    /// <exception cref="SettingsException">the file can't be read or written</exception>
    public FerrySettings SetState(string name, string? value)
    {
        if (name.StartsWith(SettingKey.Prefix, StringComparison.Ordinal))
            throw new ArgumentException($"\"{name}\" is a setting's name, not the app's state.", nameof(name));
        return Change(file =>
        {
            if (value is null)
                file.Remove(name);
            else
                file[name] = value;
        });
    }

    private static SettingKey Find(string name) =>
        SettingKey.TryFind(name, out var key) ? key : throw new SettingsException($"\"{name}\" is not a Ferry setting.");

    private FerrySettings Change(Action<Dictionary<string, string>> change)
    {
        var file = ReadFile(); // fresh, so a change made elsewhere since loading is kept
        change(file);
        Write(file);
        return Load();
    }

    private Dictionary<string, string> ReadFile()
    {
        var path = FilePath;
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            if (string.IsNullOrWhiteSpace(text))
                return new Dictionary<string, string>(StringComparer.Ordinal); // none yet, or emptied by a crash
            var file = JsonSerializer.Deserialize(text, FerryFileJson.Default.Settings) ?? [];
            // { "FERRY_THEME": null } is as good as not being there
            return file.Where(entry => entry.Value is not null).ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.Ordinal);
        }
        catch (JsonException e)
        {
            throw new SettingsException($"{path} is not a settings file Ferry can read: {e.Message} Fix it or delete it.", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"{path} could not be read: {e.Message}", e);
        }
    }

    private void Write(Dictionary<string, string> file)
    {
        var path = FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var ordered = file.OrderBy(e => e.Key, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal);
            // a temp file of this process's own (the app and the CLI may write at once), flushed to disk before it
            // replaces the old file, so a crash or a power cut leaves one whole file or the other
            var temporary = $"{path}.{Environment.ProcessId}{TempSuffix}";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ordered, FerryFileJson.Default.Settings)));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"{path} could not be saved: {e.Message}", e);
        }
    }

    /// <summary>The process's environment variables, names matched without regard to case.</summary>
    public static Dictionary<string, string> ProcessEnvironment()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
                variables[name] = value;
        }
        return variables;
    }
}
