using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Plugins;
using Ferry.Core.Settings;

namespace Ferry.App.Services;

/// <summary>
/// Newer packages of the installed plugins that came from a GitHub release: looked for after Ferry starts and when the
/// Plugins page opens, where Settings allow checking for updates. One that is found is offered on the plugin's page and
/// never installed by itself. A repository only some may read is asked with the token kept for it.
/// </summary>
public sealed class PluginUpdates(AppServices app)
{
    private static readonly TimeSpan WaitAfterStart = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AtMostEvery = TimeSpan.FromHours(1);

    // used on the UI thread only
    private readonly Dictionary<string, PluginUpdate> _found = new(StringComparer.Ordinal);
    private DateTimeOffset? _checkedAt;
    private bool _checking;

    /// <summary>What was found changed; on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>The newer package found for a plugin, if one was.</summary>
    public PluginUpdate? For(InstalledPlugin installed) => _found.GetValueOrDefault(installed.Stored.Manifest.Id);

    /// <summary>A plugin was installed again or removed: what was found for it no longer says anything.</summary>
    public void Forget(string id)
    {
        if (_found.Remove(id))
            Changed?.Invoke();
    }

    /// <summary>Look once Ferry has had time to start; what goes wrong is logged, as nobody asked.</summary>
    public async void CheckAfterStart()
    {
        await Task.Delay(WaitAfterStart);
        await CheckAsync();
    }

    /// <summary>Look for newer packages now, unless that was done lately. From the UI thread.</summary>
    public async Task CheckAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (_checking || app.IsDecoy || !app.Settings.Boolean(SettingKey.CheckUpdates) || (_checkedAt is { } at && now - at < AtMostEvery))
            return;
        var from = app.Installed.All.Select(installed => (Installed: installed, File: Source(installed))).Where(pair => pair.File is not null).ToList();
        if (from.Count == 0)
            return;
        (_checking, _checkedAt) = (true, now);
        try
        {
            using var http = new HttpFetcher(app.Settings.ConnectTimeout);
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var download = new PluginDownload(http);
            foreach (var (installed, file) in from)
            {
                var manifest = installed.Stored.Manifest;
                try
                {
                    var newer = await download.NewerAsync(file!, manifest.Id, Version.Parse(manifest.Version), Token(app, file!), limit.Token);
                    if (newer is null)
                        _found.Remove(manifest.Id);
                    else
                        _found[manifest.Id] = newer;
                }
                catch (Exception e) when (e is StageException or OperationCanceledException)
                {
                    CrashLog.Write($"looking for a newer {manifest.Name}", e);
                }
            }
        }
        finally
        {
            _checking = false;
        }
        Changed?.Invoke();
    }

    /// <summary>The GitHub release file a plugin's updates are looked for beside; null when it came from a file, or from somewhere else.</summary>
    public static GitHubReleaseFile? Source(InstalledPlugin installed) =>
        installed.Stored.UpdatesFrom is { } link ? GitHubReleaseFile.TryRead(link) : null;

    /// <summary>The access token kept for a release file's repository; null when there is none.</summary>
    public static string? Token(AppServices app, GitHubReleaseFile file) =>
        app.Secrets.Load(file.TokenSecret) is { Length: > 0 } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
}
