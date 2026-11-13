using Ferry.Core.Download;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>Settings: environment over file over default, problems reported, the file saved safely, the Go import.</summary>
public sealed class SettingsTests : IDisposable
{
    private const long MiB = 1 << 20;
    private readonly string _folder = Directory.CreateTempSubdirectory("ferry_settings_").FullName;
    private readonly Dictionary<string, string> _environment = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private AppPaths Paths => new(Path.Combine(_folder, "config"), Path.Combine(_folder, "downloads"));

    private SettingsStore Store() => new(Paths, _environment);

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(Paths.ConfigFolder);
        File.WriteAllText(Paths.SettingsFile, json);
    }

    [Fact]
    public void NewInstallUsesDefaults()
    {
        var settings = Store().Load();

        Assert.Empty(settings.Problems);
        Assert.All(settings.Values, v => Assert.Equal(SettingSource.Default, v.Source));
        Assert.Equal(Paths.DownloadFolder, settings.DownloadFolder);
        Assert.Equal(DownloadProfile.Balanced, settings.Profile);
        Assert.Equal(2, settings.FilesAtOnce);
        Assert.Equal(ExistingFile.Rename, settings.OnExisting);
        Assert.Equal(SpeedLimit.Unlimited, settings.SpeedLimit);
        Assert.Equal(DownloadOptions.For(DownloadProfile.Balanced), settings.Download);
    }

    [Fact]
    public void EnvironmentWinsOverFileWhichWinsOverDefault()
    {
        WriteFile("""{"FERRY_FILES_AT_ONCE": "3", "FERRY_DOWNLOAD_PROFILE": "aggressive"}""");
        _environment["ferry_files_at_once"] = "5";

        var settings = Store().Load();

        Assert.Equal(new SettingValue(SettingKey.FilesAtOnce, "5", SettingSource.Environment), settings[SettingKey.FilesAtOnce]);
        Assert.Equal(new SettingValue(SettingKey.DownloadProfile, "aggressive", SettingSource.File), settings[SettingKey.DownloadProfile]);
        Assert.Equal(SettingSource.Default, settings[SettingKey.OnExisting].Source);
        Assert.Equal(5, settings.FilesAtOnce);
        Assert.Equal(8, settings.Download.Connections);
    }

    [Theory]
    [InlineData("FERRY_DOWNLOAD_STALL_TIMEOUT")]
    [InlineData("download.stall_timeout")]
    [InlineData("download.stallTimeout")]
    [InlineData("DOWNLOAD_STALL_TIMEOUT")]
    [InlineData("download-stall-timeout")]
    public void SettingIsFoundByAnySpelling(string name)
    {
        Assert.True(SettingKey.TryFind(name, out var key));
        Assert.Same(SettingKey.DownloadStallTimeout, key);
    }

    [Fact]
    public void BadValuesAreReportedWithNameValueAndSource()
    {
        WriteFile("""{"FERRY_DOWNLOAD_STALL_TIMEOUT": "soon", "FERRY_FILES_AT_ONCE": "4"}""");
        _environment["FERRY_FILES_AT_ONCE"] = "99";

        var settings = Store().Load();

        Assert.Equal(2, settings.Problems.Count);
        var timeout = settings.Problems.Single(p => p.Name == "FERRY_DOWNLOAD_STALL_TIMEOUT");
        Assert.Equal(("soon", SettingSource.File), (timeout.Value, timeout.Source));
        Assert.Contains("not a time", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("30s", timeout.Message, StringComparison.Ordinal);
        var files = settings.Problems.Single(p => p.Name == "FERRY_FILES_AT_ONCE");
        Assert.Equal(SettingSource.Environment, files.Source);
        Assert.Contains("between 1 and 16", files.Message, StringComparison.Ordinal);
        // the environment's bad value falls back to the file's, not straight to the default
        Assert.Equal(4, settings.FilesAtOnce);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.Duration(SettingKey.DownloadStallTimeout));
    }

    [Fact]
    public void UnknownAndRetiredSettingsAreReported()
    {
        WriteFile("""{"FERRY_DOWNLOAD_TIMEOUT": "5m", "FERRY_NOPE": "1"}""");
        _environment["FERRY_MAX_CONCURRENT"] = "3";
        _environment[SettingsStore.PathVariable] = "";
        _environment["PATH"] = "/usr/bin";

        var problems = Store().Load().Problems;

        Assert.Equal(["FERRY_DOWNLOAD_TIMEOUT", "FERRY_MAX_CONCURRENT", "FERRY_NOPE"], problems.Select(p => p.Name).Order());
        Assert.Contains("FERRY_DOWNLOAD_STALL_TIMEOUT", problems.Single(p => p.Name == "FERRY_DOWNLOAD_TIMEOUT").Message, StringComparison.Ordinal);
        Assert.Contains("FERRY_FILES_AT_ONCE", problems.Single(p => p.Name == "FERRY_MAX_CONCURRENT").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetChecksSavesAndKeepsTheAppsState()
    {
        WriteFile("""{"lastPage": "settings"}""");
        var store = Store();

        var settings = store.Set("download.profile", "Conservative");
        store.Set("speed_limit", "10m");

        Assert.Equal("conservative", settings.Text(SettingKey.DownloadProfile));
        settings = store.Load();
        Assert.Equal(10 * MiB, settings.SpeedLimit);
        Assert.Equal("settings", settings.State["lastPage"]);
        Assert.Equal([Path.GetFileName(Paths.SettingsFile)], Directory.GetFiles(Paths.ConfigFolder).Select(Path.GetFileName));

        var refused = Assert.Throws<SettingsException>(() => store.Set("FERRY_ON_EXISTING", "ask"));
        Assert.Contains("rename, skip, overwrite", refused.Message, StringComparison.Ordinal);
        Assert.Throws<SettingsException>(() => store.Set("FERRY_NOPE", "1"));

        settings = store.Unset("FERRY_DOWNLOAD_PROFILE");
        Assert.Equal(SettingSource.Default, settings[SettingKey.DownloadProfile].Source);
        Assert.Equal(SettingSource.File, settings[SettingKey.SpeedLimit].Source);
    }

    [Fact]
    public void AppStateIsSavedBesideTheSettings()
    {
        var store = Store();
        store.Set("FERRY_THEME", "dark");

        var settings = store.SetState("skippedVersion", "1.2.0");

        Assert.Equal("1.2.0", settings.State["skippedVersion"]);
        Assert.Equal("Dark", settings.Text(SettingKey.Theme));
        Assert.Empty(store.SetState("skippedVersion", null).State);
        Assert.Throws<ArgumentException>(() => store.SetState("FERRY_THEME", "x"));
    }

    [Fact]
    public void UnreadableFileIsReportedAndNeverOverwritten()
    {
        WriteFile("{ not json");
        var store = Store();

        var settings = store.Load();

        Assert.Equal(Paths.SettingsFile, Assert.Single(settings.Problems).Name);
        Assert.Equal(2, settings.FilesAtOnce);
        Assert.Throws<SettingsException>(() => store.Set("FERRY_FILES_AT_ONCE", "3"));
        Assert.Equal("{ not json", File.ReadAllText(Paths.SettingsFile));
    }

    [Fact]
    public void ConfigPathVariablePicksTheFile()
    {
        var elsewhere = Path.Combine(_folder, "elsewhere", "ferry.json");
        _environment[SettingsStore.PathVariable] = elsewhere;

        Store().Set("FERRY_FILES_AT_ONCE", "4");

        Assert.True(File.Exists(elsewhere));
        Assert.False(File.Exists(Paths.SettingsFile));
        Assert.Empty(Store().Load().Problems); // FERRY_CONFIG_PATH is not mistaken for an unknown setting
    }

    [Fact]
    public void ConnectionsAndPartSizeOverrideTheProfileOnlyWhenSet()
    {
        var store = Store();
        store.Set("FERRY_DOWNLOAD_PROFILE", "conservative");

        Assert.Equal((2, 4 * MiB), (store.Load().Download.Connections, store.Load().Download.MinSegmentBytes));

        store.Set("FERRY_DOWNLOAD_THREADS", "40");
        store.Set("FERRY_DOWNLOAD_MIN_SPLIT_SIZE", "512K");
        store.Set("FERRY_DOWNLOAD_STALL_TIMEOUT", "1m30s");
        var download = store.Load().Download;

        Assert.Equal(16, download.Connections); // capped by FERRY_DOWNLOAD_MAX_THREADS
        Assert.Equal(512 << 10, download.MinSegmentBytes);
        Assert.Equal(TimeSpan.FromSeconds(90), download.StallTimeout);

        store.Set("FERRY_DOWNLOAD_MAX_THREADS", "32");
        Assert.Equal(32, store.Load().Download.Connections);
    }

    [Theory]
    [InlineData("30s", 30_000)]
    [InlineData("2m", 120_000)]
    [InlineData("1m30s", 90_000)]
    [InlineData("500ms", 500)]
    [InlineData("1h", 3_600_000)]
    [InlineData("45", 45_000)]
    [InlineData("1.5s", 1_500)]
    public void DurationsAreRead(string text, long milliseconds)
    {
        Assert.True(SettingKey.TryParseDuration(text, out var duration));
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), duration);
    }

    [Fact]
    public void FoldersAreSavedInFull()
    {
        Assert.True(SettingKey.DownloadDir.TryNormalise("dl", out var relative, out _));
        Assert.Equal(Path.GetFullPath("dl"), relative);
        Assert.True(SettingKey.DownloadDir.TryNormalise("~/Downloads", out var home, out _));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), home);
        Assert.True(SettingKey.DownloadDir.TryNormalise("", out var none, out _));
        Assert.Equal("", none);
    }

    [Theory]
    [InlineData("FERRY_BROWSER_USER_AGENT", "Mozilla/5.0 (X11")] // would make every request throw
    [InlineData("FERRY_BROWSER_USER_AGENT", "")]
    [InlineData("FERRY_BROWSER_USER_AGENT", "a\r\nX-Injected: 1")]
    [InlineData("FERRY_SERVER_FOLDER", "")]
    public void TextThatWouldBreakThingsIsRefused(string name, string text)
    {
        Assert.True(SettingKey.TryFind(name, out var key));
        Assert.False(key.TryNormalise(text, out _, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("5x")]
    [InlineData("-3")]
    [InlineData("m")]
    [InlineData("99999999999h")] // past what TimeSpan holds: refused, not an OverflowException
    [InlineData("1e300")]
    [InlineData("nan")]
    [InlineData("infinity")]
    public void BadDurationsAreRefused(string text) => Assert.False(SettingKey.TryParseDuration(text, out _));

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1024", 1024)]
    [InlineData("500K", 500 << 10)]
    [InlineData("10M", 10 << 20)]
    [InlineData("10mb", 10 << 20)]
    [InlineData("1GiB", 1 << 30)]
    [InlineData("1.5M", 3 << 19)]
    public void SizesAreRead(string text, long bytes)
    {
        Assert.True(SettingKey.TryParseSize(text, out var parsed));
        Assert.Equal(bytes, parsed);
    }

    [Fact]
    public void ServerModeSettingsAndWhatTravelsToTheServer()
    {
        var store = Store();
        Assert.Equal(DownloadMode.Local, store.Load().Mode);

        store.Set("plugin.server", "true");
        store.Set("mode", "Server");
        store.Set("server.port", "2222");
        store.Set("transfer_limit", "5M");
        _environment["FERRY_SERVER_KEEP_FILES"] = "yes";
        var settings = store.Load();

        Assert.Equal(DownloadMode.Server, settings.Mode);
        Assert.Equal(5 * MiB, settings.TransferLimit);
        Assert.True(settings.KeepServerFiles);
        Assert.Equal(2, settings.TransfersAtOnce);
        Assert.Equal("~/.ferry/downloads", settings.Text(ServerKeys.ServerFolder));
        Assert.Equal(new Dictionary<string, string>
        {
            ["FERRY_PLUGIN_SERVER"] = "true",
            ["FERRY_MODE"] = "server",
            ["FERRY_SERVER_PORT"] = "2222",
            ["FERRY_TRANSFER_LIMIT"] = "5M",
            ["FERRY_SERVER_KEEP_FILES"] = "true",
        }, settings.Changed());
        Assert.Throws<SettingsException>(() => store.Set("FERRY_SERVER_PORT", "70000"));
    }

    [Fact]
    public void ScheduleCountsOnlyWhenOnAndItsTimesAreChecked()
    {
        var store = Store();
        store.Set("schedule.start", "22:30");
        store.Set("schedule.stop", "6:15");

        Assert.Null(store.Load().Schedule);
        store.Set("schedule.on", "true");

        Assert.Equal(new DownloadSchedule(new TimeOnly(22, 30), new TimeOnly(6, 15)), store.Load().Schedule);
        Assert.Equal("06:15", store.Load().Text(SettingKey.ScheduleStop));
        Assert.Throws<SettingsException>(() => store.Set("schedule.stop", "25:00"));
    }

    [Fact]
    public void PathsArePerUser()
    {
        var paths = AppPaths.ForApp();

        Assert.True(Path.IsPathFullyQualified(paths.ConfigFolder));
        Assert.Equal(OperatingSystem.IsWindows() ? "Ferry" : "ferry", Path.GetFileName(paths.ConfigFolder));
        Assert.Equal("Ferry", Path.GetFileName(paths.DownloadFolder));
        Assert.Equal(Path.Combine(paths.ConfigFolder, "settings.json"), paths.SettingsFile);
    }
}
