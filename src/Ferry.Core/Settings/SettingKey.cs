using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Ferry.Core.Text;

namespace Ferry.Core.Settings;

/// <summary>What kind of value a setting holds, which decides how its text is read and checked.</summary>
public enum SettingType
{
    Text,

    /// <summary>A User-Agent header value, which every request would fail on if it were malformed.</summary>
    UserAgent,

    Url,

    /// <summary>Host names, separated by commas or spaces.</summary>
    Hosts,

    WholeNumber,

    /// <summary>"30s", "2m", "1m30s", "500ms", "1h"; a bare number is seconds.</summary>
    Duration,

    /// <summary>Bytes: "10M", "500K", "1G" (powers of 1024); a bare number is bytes.</summary>
    Size,

    Boolean,
    Choice,
    Folder,

    /// <summary>"23:00".</summary>
    TimeOfDay,
}

/// <summary>
/// One setting: its environment name, type, default and limits. <see cref="All"/> is the one list of settings; the
/// settings file, the environment, the CLI editor and the app's Settings page all read it.
/// </summary>
/// <param name="Name">the environment variable and settings-file key ("FERRY_FILES_AT_ONCE")</param>
/// <param name="Default">the value as text; empty for "none", or for a default the front end decides
/// (<see cref="DownloadDir"/>)</param>
/// <param name="Shown">on the app's Settings page and the CLI editor's first screen; the rest are "Advanced"</param>
/// <param name="AppOnly">only the Windows app reads it</param>
public sealed record SettingKey(string Name, SettingType Type, string Default, string Help, bool Shown = false,
                                bool AppOnly = false)
{
    public const string Prefix = "FERRY_";

    /// <summary>What people call it, for the app's Settings page ("Files at once").</summary>
    public string Title { get; init; } = Name;

    /// <summary>For <see cref="SettingType.Choice"/>: the allowed values, as they are written back.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>For <see cref="SettingType.WholeNumber"/> and <see cref="SettingType.Size"/>.</summary>
    public long Min { get; init; }

    public long Max { get; init; } = long.MaxValue;

    /// <summary>For <see cref="SettingType.Duration"/>.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.Zero;

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromDays(1);

    private const string TimeOfDayFormat = "HH:mm";
    private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    public static readonly SettingKey DownloadDir = new("FERRY_DOWNLOAD_DIR", SettingType.Folder, "",
        "Where downloads are saved.", Shown: true)
    { Title = "Download folder" };

    public static readonly SettingKey DownloadProfile = new("FERRY_DOWNLOAD_PROFILE", SettingType.Choice, "balanced",
        "How many connections each file uses: conservative (2), balanced (4) or aggressive (8).", Shown: true)
    {
        Title = "Speed profile",
        Choices = ["conservative", "balanced", "aggressive"],
    };

    public static readonly SettingKey DownloadThreads = new("FERRY_DOWNLOAD_THREADS", SettingType.WholeNumber, "0",
        "Connections per file; 0 takes the profile's number.", Shown: true)
    {
        Title = "Connections per file",
        Min = 0,
        Max = 64
    };

    public static readonly SettingKey DownloadMaxThreads = new("FERRY_DOWNLOAD_MAX_THREADS", SettingType.WholeNumber, "16",
        "The most connections one file may use.")
    {
        Title = "Most connections per file",
        Min = 1,
        Max = 64
    };

    public static readonly SettingKey FilesAtOnce = new("FERRY_FILES_AT_ONCE", SettingType.WholeNumber, "2",
        "Files downloaded at the same time.", Shown: true)
    {
        Title = "Files at once",
        Min = 1,
        Max = 16
    };

    public static readonly SettingKey ConnectionsPerHost = new("FERRY_CONNECTIONS_PER_HOST", SettingType.WholeNumber, "16",
        "The most connections to one site at a time, over all its files.")
    {
        Title = "Connections per server",
        Min = 1,
        Max = 256
    };

    public static readonly SettingKey OnExisting = new("FERRY_ON_EXISTING", SettingType.Choice, "rename",
        "When the file is already there: rename (save as \"name (1).ext\"), skip or overwrite.", Shown: true)
    {
        Title = "When a file is already there",
        Choices = ["rename", "skip", "overwrite"],
    };

    public static readonly SettingKey DownloadStallTimeout = new("FERRY_DOWNLOAD_STALL_TIMEOUT", SettingType.Duration, "30s",
        "A connection that receives nothing for this long is tried again. Downloads have no overall time limit.")
    {
        Title = "Stall timeout",
        MinDuration = TimeSpan.FromSeconds(1),
        MaxDuration = TimeSpan.FromHours(1),
    };

    public static readonly SettingKey DownloadConnectTimeout = new("FERRY_DOWNLOAD_CONNECT_TIMEOUT", SettingType.Duration, "15s",
        "How long to wait for a server to accept a connection.")
    {
        Title = "Connect timeout",
        MinDuration = TimeSpan.FromSeconds(1),
        MaxDuration = TimeSpan.FromMinutes(10),
    };

    public static readonly SettingKey DownloadMinSplitSize = new("FERRY_DOWNLOAD_MIN_SPLIT_SIZE", SettingType.Size, "1M",
        "The smallest piece of a file that gets a connection of its own. When not set, the profile decides.")
    {
        Title = "Smallest part per connection",
        Min = 64 * Units.KiB,
        Max = Units.GiB
    };

    public static readonly SettingKey DownloadRetries = new("FERRY_DOWNLOAD_RETRIES", SettingType.WholeNumber, "3",
        "How many times a failed connection is tried again. The count starts over whenever it makes progress.")
    {
        Title = "Retries per connection",
        Min = 0,
        Max = 100
    };

    public static readonly SettingKey DownloadRetryWait = new("FERRY_DOWNLOAD_RETRY_WAIT", SettingType.Duration, "1s",
        "The pause before a connection is tried again; it grows with each try.")
    {
        Title = "Wait before a retry",
        MaxDuration = TimeSpan.FromMinutes(10)
    };

    public static readonly SettingKey BrowserUserAgent = new("FERRY_BROWSER_USER_AGENT", SettingType.UserAgent, DefaultUserAgent,
        "The browser Ferry says it is when it asks a site for a file.")
    { Title = "Browser user agent" };

    public static readonly SettingKey SpeedLimit = new("FERRY_SPEED_LIMIT", SettingType.Size, "0",
        "Overall speed limit per second, such as 10M or 500K; 0 for none.", Shown: true)
    {
        Title = "Speed limit",
        Min = 0,
        Max = 100 * Units.GiB
    };

    public static readonly SettingKey LogLevel = new("FERRY_LOG_LEVEL", SettingType.Choice, "info",
        "How much is logged; debug also says how long each download took.")
    {
        Title = "Log level",
        Choices = ["debug", "info", "warn", "error"]
    };

    public static readonly SettingKey CheckUpdates = new("FERRY_CHECK_UPDATES", SettingType.Boolean, "true",
        "Look for a new version once a day.", Shown: true, AppOnly: true)
    { Title = "Check for updates" };

    public static readonly SettingKey SortByType = new("FERRY_SORT_BY_TYPE", SettingType.Boolean, "false",
        "Videos, music, documents and so on each go into a folder of their own inside the download folder.", Shown: true)
    { Title = "Sort downloads by type" };

    public static readonly SettingKey ScheduleOn = new("FERRY_SCHEDULE_ON", SettingType.Boolean, "false",
        "Download on this PC only between the hours below; outside them, files wait. In server mode the server keeps downloading, and only the transfers to this PC wait.", Shown: true, AppOnly: true)
    { Title = "Download on a schedule" };

    public static readonly SettingKey ScheduleStart = new("FERRY_SCHEDULE_START", SettingType.TimeOfDay, "23:00",
        "The time downloads may start, as 23:00.", Shown: true, AppOnly: true)
    { Title = "Schedule starts at" };

    public static readonly SettingKey ScheduleStop = new("FERRY_SCHEDULE_STOP", SettingType.TimeOfDay, "07:00",
        "The time downloads stop, keeping what they have, as 07:00.", Shown: true, AppOnly: true)
    { Title = "Schedule stops at" };

    public static readonly SettingKey CloseToTray = new("FERRY_CLOSE_TO_TRAY", SettingType.Boolean, "false",
        "Closing the window keeps Ferry running in the notification area, downloading.", Shown: true, AppOnly: true)
    { Title = "Keep running when closed" };

    public static readonly SettingKey NotifyWhenDone = new("FERRY_NOTIFY_WHEN_DONE", SettingType.Boolean, "true",
        "Show a notification when the queue finishes while Ferry is not in front.", Shown: true, AppOnly: true)
    { Title = "Notify when downloads finish" };

    public static readonly SettingKey AskBeforeAdding = new("FERRY_ASK_BEFORE_ADDING", SettingType.Boolean, "true",
        "Show each link's name and size, and choose where it goes, before it is added.", Shown: true, AppOnly: true)
    { Title = "Ask before adding links" };

    public static readonly SettingKey WatchClipboard = new("FERRY_WATCH_CLIPBOARD", SettingType.Boolean, "false",
        "Offer to add links when you copy them.", Shown: true, AppOnly: true)
    { Title = "Watch the clipboard for links" };

    public static readonly SettingKey LibraryFolder = new("FERRY_LIBRARY_FOLDER", SettingType.Folder, "",
        "The folder your library is in, each title in a folder or archive of its own, named with its version: \"Name (v1.2)\".", AppOnly: true)
    { Title = "Library folder" };

    public static readonly SettingKey Theme = new("FERRY_THEME", SettingType.Choice, "System",
        "Light or dark, or as Windows is.", Shown: true, AppOnly: true)
    {
        Title = "Theme",
        Choices = ["System", "Light", "Dark"]
    };

    public static readonly SettingKey Accent = new("FERRY_ACCENT", SettingType.Choice, "Tide",
        "The colour of progress bars, graphs and anything live. Everything else stays in ink.", Shown: true, AppOnly: true)
    {
        Title = "Accent",
        Choices = ["Tide", "Signal", "Iris", "Windows"]
    };

    /// <summary>Settings that are gone, and what became of them.</summary>
    public static IReadOnlyDictionary<string, string> Retired { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["FERRY_DOWNLOAD_TIMEOUT"] = "Downloads have no overall time limit now; see FERRY_DOWNLOAD_STALL_TIMEOUT.",
        ["FERRY_DOWNLOAD_LOWEST_SPEED"] = "Replaced by FERRY_DOWNLOAD_STALL_TIMEOUT.",
        ["FERRY_MAX_CONCURRENT"] = "Replaced by FERRY_FILES_AT_ONCE.",
        ["FERRY_DEBUG_URL"] = "No longer used.",
        ["FERRY_IP_CHECK_URL"] = "No longer used.",
        ["FERRY_IP_CHECK_TIMEOUT"] = "No longer used.",
        ["FERRY_MAX_IDLE_CONNS"] = "No longer used.",
        ["FERRY_MAX_IDLE_CONNS_PER_HOST"] = "No longer used.",
    };

    /// <summary>Ferry's own settings, in the order they are shown, without any plugin's.</summary>
    public static IReadOnlyList<SettingKey> Standard { get; } =
    [
        DownloadDir, DownloadProfile, DownloadThreads, FilesAtOnce, OnExisting, SpeedLimit, SortByType, ScheduleOn, ScheduleStart, ScheduleStop,
        CheckUpdates, AskBeforeAdding, WatchClipboard, CloseToTray, NotifyWhenDone, LibraryFolder, Theme, Accent,
        DownloadMaxThreads, ConnectionsPerHost, DownloadStallTimeout, DownloadConnectTimeout, DownloadMinSplitSize,
        DownloadRetries, DownloadRetryWait, BrowserUserAgent, LogLevel,
    ];

    // replaced whole on each addition, never changed: a plugin installed while the app runs adds its settings as others read these
    private static readonly Lock Adding = new();
    private static volatile SettingKey[] s_keys = [.. Standard];
    private static volatile Dictionary<string, SettingKey> s_byName = s_keys.ToDictionary(k => k.Name, StringComparer.Ordinal);

    /// <summary>Every setting, Ferry's own and then the plugins'.</summary>
    public static IReadOnlyList<SettingKey> All => s_keys;

    /// <summary>A plugin's settings, in the one table, before any of them is read. A name that is there already stays as it is.</summary>
    /// <param name="replace">a name that is there already becomes this one: for a plugin installed again, whose settings may have changed</param>
    public static void Contribute(IEnumerable<SettingKey> keys, bool replace = false)
    {
        lock (Adding)
        {
            var given = keys.DistinctBy(key => key.Name).ToList();
            var fresh = given.Where(key => !s_byName.ContainsKey(key.Name)).ToList();
            var changed = replace ? given.Where(key => s_byName.TryGetValue(key.Name, out var known) && known != key).ToDictionary(key => key.Name) : [];
            if (fresh.Count == 0 && changed.Count == 0)
                return;
            SettingKey[] all = [.. s_keys.Select(key => changed.GetValueOrDefault(key.Name, key)), .. fresh];
            s_byName = all.ToDictionary(key => key.Name, StringComparer.Ordinal);
            s_keys = all;
        }
    }

    /// <summary>
    /// The setting called <paramref name="name"/>, in any of its spellings: "FERRY_FILES_AT_ONCE",
    /// "files.at.once", "files_at_once", or "filesAtOnce".
    /// </summary>
    public static bool TryFind(string name, [NotNullWhen(true)] out SettingKey? key) =>
        s_byName.TryGetValue(Canonical(name), out key);

    /// <summary>"filesAtOnce" → "FERRY_FILES_AT_ONCE".</summary>
    public static string Canonical(string name)
    {
        var builder = new StringBuilder(name.Length + Prefix.Length);
        var trimmed = name.Trim();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is '.' or '-' or ' ')
                c = '_';
            else if (char.IsUpper(c) && i > 0 && char.IsLower(trimmed[i - 1]))
                builder.Append('_');
            builder.Append(char.ToUpperInvariant(c));
        }
        var canonical = builder.ToString();
        return canonical.StartsWith(Prefix, StringComparison.Ordinal) ? canonical : Prefix + canonical;
    }

    /// <summary>
    /// Check <paramref name="text"/> for this setting, and give it back as it will be stored (a choice in its own
    /// spelling, hosts lower-case and comma-separated, other values trimmed).
    /// </summary>
    /// <param name="reason">why the value can't be used, for the user</param>
    public bool TryNormalise(string text, [NotNullWhen(true)] out string? value, [NotNullWhen(false)] out string? reason)
    {
        var trimmed = text.Trim();
        (value, reason) = (null, null);
        switch (Type)
        {
            case SettingType.Text or SettingType.UserAgent:
                if (trimmed.Any(char.IsControl))
                {
                    reason = "has a line break or another control character in it";
                    return false;
                }
                if (trimmed.Length == 0 && Default.Length > 0)
                {
                    reason = "can't be empty (remove it to use the default)";
                    return false;
                }
                if (Type == SettingType.UserAgent && !IsUserAgent(trimmed))
                {
                    reason = "is not a user agent, such as \"Mozilla/5.0 (Windows NT 10.0; Win64; x64)\"";
                    return false;
                }
                value = trimmed;
                return true;
            case SettingType.Url:
                if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                {
                    value = trimmed;
                    return true;
                }
                reason = "is not a web address (https://...)";
                return false;
            case SettingType.Hosts:
                var hosts = trimmed.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                                   .Select(h => h.ToLowerInvariant()).Distinct().ToList();
                if (hosts.Count == 0 || hosts.Any(h => Uri.CheckHostName(h) is not (UriHostNameType.Dns or UriHostNameType.IPv4)))
                {
                    reason = "is not a list of host names, such as example.com,example.org";
                    return false;
                }
                value = string.Join(',', hosts);
                return true;
            case SettingType.Folder:
                if (trimmed.Any(char.IsControl))
                {
                    reason = "has a line break or another control character in it";
                    return false;
                }
                // in full, so every process (started from anywhere) means the same folder
                value = trimmed.Length == 0 ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(ExpandHome(trimmed)));
                return true;
            case SettingType.WholeNumber:
                if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    reason = "is not a whole number";
                    return false;
                }
                return InRange(number, number.ToString(CultureInfo.InvariantCulture), out value, out reason);
            case SettingType.Size:
                if (!TryParseSize(trimmed, out var bytes))
                {
                    reason = "is not a size, such as 500K, 10M or 1G";
                    return false;
                }
                return InRange(bytes, trimmed.ToUpperInvariant(), out value, out reason);
            case SettingType.Duration:
                if (!TryParseDuration(trimmed, out var duration))
                {
                    reason = "is not a time, such as 30s, 2m or 1m30s";
                    return false;
                }
                if (duration < MinDuration || duration > MaxDuration)
                {
                    reason = $"is not between {FormatDuration(MinDuration)} and {FormatDuration(MaxDuration)}";
                    return false;
                }
                value = trimmed.ToLowerInvariant();
                return true;
            case SettingType.TimeOfDay:
                if (!TryParseTimeOfDay(trimmed, out var time))
                {
                    reason = "is not a time of day, such as 23:00";
                    return false;
                }
                value = time.ToString(TimeOfDayFormat, CultureInfo.InvariantCulture);
                return true;
            case SettingType.Boolean:
                if (!TryParseBoolean(trimmed, out var flag))
                {
                    reason = "is not true or false";
                    return false;
                }
                value = flag ? "true" : "false";
                return true;
            case SettingType.Choice:
                value = Choices.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
                if (value is not null)
                    return true;
                reason = $"is not one of {string.Join(", ", Choices)}";
                return false;
            default:
                throw new UnreachableException($"No rule for {Type} settings.");
        }
    }

    /// <summary>"~/x" as a folder under the user's home.</summary>
    private static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;

    private static bool IsUserAgent(string text)
    {
        using var probe = new System.Net.Http.HttpRequestMessage();
        return probe.Headers.UserAgent.TryParseAdd(text);
    }

    private bool InRange(long number, string text, [NotNullWhen(true)] out string? value, [NotNullWhen(false)] out string? reason)
    {
        (value, reason) = (null, null);
        if (number < Min || number > Max)
        {
            reason = Type == SettingType.Size
                ? $"is not between {FormatSize(Min)} and {FormatSize(Max)}"
                : $"is not between {Min} and {Max}";
            return false;
        }
        value = text;
        return true;
    }

    /// <summary>"30s", "2m", "1h", "500ms", "1m30s", or a bare number of seconds.</summary>
    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var rest = text.Trim().ToLowerInvariant();
        if (rest.Length == 0)
            return false;
        if (double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return TrySeconds(seconds, out duration);
        var total = 0.0;
        while (rest.Length > 0)
        {
            var digits = 0;
            while (digits < rest.Length && (char.IsAsciiDigit(rest[digits]) || rest[digits] == '.'))
                digits++;
            var units = digits;
            while (units < rest.Length && char.IsAsciiLetter(rest[units]))
                units++;
            if (digits == 0 || units == digits
                || !double.TryParse(rest.AsSpan(0, digits), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                return false;
            var unit = rest[digits..units] switch
            {
                "ms" => 0.001,
                "s" => 1,
                "m" => 60,
                "h" => 3600,
                _ => (double?)null,
            };
            if (unit is null)
                return false;
            total += amount * unit.Value;
            rest = rest[units..];
        }
        return TrySeconds(total, out duration);
    }

    /// <summary>A duration of <paramref name="seconds"/>, if that is one: not negative, not NaN, not too long for TimeSpan.</summary>
    private static bool TrySeconds(double seconds, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds / 2)
            return false;
        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>"500K", "10M", "1G" (also "10MB", "10MiB"), or a bare number of bytes.</summary>
    public static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        var trimmed = text.Trim().ToUpperInvariant();
        if (trimmed.EndsWith("IB", StringComparison.Ordinal))
            trimmed = trimmed[..^2];
        else if (trimmed.EndsWith('B'))
            trimmed = trimmed[..^1];
        if (trimmed.Length == 0)
            return false;
        var unit = trimmed[^1] switch
        {
            'K' => Units.KiB,
            'M' => Units.MiB,
            'G' => Units.GiB,
            _ => 1L,
        };
        var number = unit == 1 ? trimmed : trimmed[..^1];
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount < 0
            || amount * unit > long.MaxValue)
            return false;
        bytes = (long)(amount * unit);
        return true;
    }

    /// <summary>"23:00" or "7:30".</summary>
    public static bool TryParseTimeOfDay(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), [TimeOfDayFormat, "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static bool TryParseBoolean(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "on" or "1":
                value = true;
                return true;
            case "false" or "no" or "off" or "0":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 && duration.TotalHours % 1 == 0 ? $"{duration.TotalHours:0}h"
        : duration.TotalMinutes >= 1 && duration.TotalMinutes % 1 == 0 ? $"{duration.TotalMinutes:0}m"
        : duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:0.#}s"
        : $"{duration.TotalMilliseconds:0}ms";

    public static string FormatSize(long bytes) =>
        bytes >= Units.GiB && bytes % Units.GiB == 0 ? $"{bytes / Units.GiB}G"
        : bytes >= Units.MiB && bytes % Units.MiB == 0 ? $"{bytes / Units.MiB}M"
        : bytes >= Units.KiB && bytes % Units.KiB == 0 ? $"{bytes / Units.KiB}K"
        : bytes.ToString(CultureInfo.InvariantCulture);
}
