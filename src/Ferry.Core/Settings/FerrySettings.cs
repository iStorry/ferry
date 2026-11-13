using System.Globalization;
using Ferry.Core.Download;
using Ferry.Core.Http;
using Ferry.Core.Queue;

namespace Ferry.Core.Settings;

/// <summary>Where a setting's value came from.</summary>
public enum SettingSource
{
    Default,
    File,
    Environment,
}

/// <param name="Text">the value as stored, already checked</param>
public sealed record SettingValue(SettingKey Key, string Text, SettingSource Source);

/// <summary>A value that was not used, and why; the setting falls back to the next source.</summary>
/// <param name="Name">the name as it was written</param>
public sealed record SettingProblem(string Name, string Value, SettingSource Source, string Reason)
{
    public string Message => Source switch
    {
        SettingSource.Environment => $"{Name}=\"{Value}\" (environment) {Reason}.",
        SettingSource.File => $"{Name}=\"{Value}\" (settings file) {Reason}.",
        _ => $"{Name} {Reason}.",
    };
}

/// <summary>
/// The settings in effect: each one from the environment, else the settings file, else its default. A value that
/// can't be used is reported in <see cref="Problems"/> and the next source's value is used instead.
/// </summary>
public sealed class FerrySettings
{
    private readonly Dictionary<SettingKey, SettingValue> _values;

    private FerrySettings(AppPaths paths, Dictionary<SettingKey, SettingValue> values, IReadOnlyList<SettingProblem> problems,
                           IReadOnlyDictionary<string, string> state)
    {
        (Paths, _values, Problems, State) = (paths, values, problems, state);
    }

    public AppPaths Paths { get; }

    public IReadOnlyList<SettingProblem> Problems { get; }

    /// <summary>What the app remembers in the settings file besides settings (keys without the FERRY_ prefix).</summary>
    public IReadOnlyDictionary<string, string> State { get; }

    /// <summary>
    /// The setting as it is here. One that became a setting after these were read, as a plugin installed since has, is at
    /// its default: these are how things were when they were read.
    /// </summary>
    public SettingValue this[SettingKey key] => _values.TryGetValue(key, out var value) ? value : new SettingValue(key, key.Default, SettingSource.Default);

    /// <summary>Every setting in <see cref="SettingKey.All"/> order.</summary>
    public IEnumerable<SettingValue> Values => SettingKey.All.Select(key => this[key]);

    /// <summary>Settings from defaults alone, as on a new install.</summary>
    public static FerrySettings Defaults(AppPaths paths) =>
        Build(paths, new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <param name="file">the settings file's entries</param>
    /// <param name="environment">environment variables; names are matched without regard to case</param>
    /// <param name="fileProblems">problems found reading the file, reported with the rest</param>
    public static FerrySettings Build(AppPaths paths, IReadOnlyDictionary<string, string> file,
                                       IReadOnlyDictionary<string, string> environment, IEnumerable<SettingProblem>? fileProblems = null)
    {
        var problems = new List<SettingProblem>(fileProblems ?? []);
        var fromFile = Read(file, SettingSource.File, problems, out var state);
        var fromEnvironment = Read(environment.Where(e => IsSettingName(e.Key) && !string.Equals(e.Key, SettingsStore.PathVariable, StringComparison.OrdinalIgnoreCase)),
                                   SettingSource.Environment, problems, out _);
        var values = SettingKey.All.ToDictionary(k => k, k =>
            fromEnvironment.TryGetValue(k, out var e) ? e
            : fromFile.TryGetValue(k, out var f) ? f
            : new SettingValue(k, k.Default, SettingSource.Default));
        return new FerrySettings(paths, values, problems, state);
    }

    private static Dictionary<SettingKey, SettingValue> Read(IEnumerable<KeyValuePair<string, string>> entries, SettingSource source,
                                                             List<SettingProblem> problems, out IReadOnlyDictionary<string, string> state)
    {
        var values = new Dictionary<SettingKey, SettingValue>();
        var other = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, text) in entries)
        {
            if (source == SettingSource.File && !IsSettingName(name))
            {
                other[name] = text;
                continue;
            }
            var canonical = name.ToUpperInvariant();
            if (SettingKey.Retired.TryGetValue(canonical, out var replaced))
                problems.Add(new SettingProblem(name, text, source, $"is no longer a setting, so it is ignored. {replaced}"));
            else if (!SettingKey.TryFind(canonical, out var key))
                problems.Add(new SettingProblem(name, text, source, "is not a Ferry setting, so it is ignored"));
            else if (!key.TryNormalise(text, out var value, out var reason))
                problems.Add(new SettingProblem(name, text, source, $"{reason}; using {Fallback(key, source)} instead"));
            else
                values[key] = new SettingValue(key, value, source);
        }
        state = other;
        return values;
    }

    private static bool IsSettingName(string name) => name.StartsWith(SettingKey.Prefix, StringComparison.OrdinalIgnoreCase);

    private static string Fallback(SettingKey key, SettingSource source) =>
        source == SettingSource.Environment ? "the settings file's value or the default"
        : key.Default.Length == 0 ? "the default" : $"the default, {key.Default}";

    public string Text(SettingKey key) => this[key].Text;

    public int Number(SettingKey key) => (int)Math.Clamp(long.Parse(Text(key), CultureInfo.InvariantCulture), int.MinValue, int.MaxValue);

    public long Size(SettingKey key) => SettingKey.TryParseSize(Text(key), out var bytes) ? bytes : 0;

    public TimeSpan Duration(SettingKey key) => SettingKey.TryParseDuration(Text(key), out var duration) ? duration : TimeSpan.Zero;

    public bool Boolean(SettingKey key) => SettingKey.TryParseBoolean(Text(key), out var flag) && flag;

    public Uri Url(SettingKey key) => new(Text(key));

    /// <summary>Where downloads go: the setting, else the front end's default folder.</summary>
    public string DownloadFolder =>
        Text(SettingKey.DownloadDir) is { Length: > 0 } folder ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder))
                                                              : Paths.DownloadFolder;

    /// <summary>
    /// The settings that were set (not defaults), by name: what the PC sends to the server's agent, which keeps no
    /// settings of its own.
    /// </summary>
    public Dictionary<string, string> Changed() =>
        Values.Where(v => v.Source != SettingSource.Default).ToDictionary(v => v.Key.Name, v => v.Text, StringComparer.Ordinal);

    public DownloadProfile Profile => Enum.Parse<DownloadProfile>(Text(SettingKey.DownloadProfile), ignoreCase: true);

    public ExistingFile OnExisting => Enum.Parse<ExistingFile>(Text(SettingKey.OnExisting), ignoreCase: true);

    public int FilesAtOnce => Number(SettingKey.FilesAtOnce);

    public int ConnectionsPerHost => Number(SettingKey.ConnectionsPerHost);

    /// <summary>Bytes per second for all downloads together, or <see cref="SpeedLimit.Unlimited"/>.</summary>
    public long SpeedLimit => Size(SettingKey.SpeedLimit);

    public TimeSpan ConnectTimeout => Duration(SettingKey.DownloadConnectTimeout);

    public string BrowserUserAgent => Text(SettingKey.BrowserUserAgent);

    /// <summary>The hours downloads may run, or null when they always may.</summary>
    public DownloadSchedule? Schedule =>
        Boolean(SettingKey.ScheduleOn) && SettingKey.TryParseTimeOfDay(Text(SettingKey.ScheduleStart), out var start)
                                       && SettingKey.TryParseTimeOfDay(Text(SettingKey.ScheduleStop), out var stop)
            ? new DownloadSchedule(start, stop)
            : null;

    /// <summary>
    /// How each file is downloaded: the profile's connections and part size, unless connections or the part size
    /// were set themselves.
    /// </summary>
    public DownloadOptions Download
    {
        get
        {
            var connections = Number(SettingKey.DownloadThreads);
            var options = DownloadOptions.For(Profile, connections == 0 ? null : connections, OnExisting,
                                              Number(SettingKey.DownloadMaxThreads));
            return options with
            {
                MinSegmentBytes = this[SettingKey.DownloadMinSplitSize].Source == SettingSource.Default
                    ? options.MinSegmentBytes
                    : Size(SettingKey.DownloadMinSplitSize),
                StallTimeout = Duration(SettingKey.DownloadStallTimeout),
                Retry = RetryPolicy.Untimed(Number(SettingKey.DownloadRetries), Duration(SettingKey.DownloadRetryWait)),
                SortByType = Boolean(SettingKey.SortByType),
            };
        }
    }
}
