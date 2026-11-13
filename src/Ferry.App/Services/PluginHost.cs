using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Queue;
using Ferry.Core.Sites;
using Ferry.Plugins.AfterDownload;
using Ferry.Plugins.BrowserLink;
using Ferry.Plugins.Feeds;
using Ferry.Plugins.Notifications;

namespace Ferry.App.Services;

/// <summary>What plugins do in the background while the app runs: read feeds, work on finished files, send word of them, and let browsers in.</summary>
/// <param name="notify">shows something on the notification area: a title and a message</param>
public sealed class PluginHost(AppServices app, Action<string, string> notify) : IDisposable
{
    private static readonly TimeSpan QueueWait = TimeSpan.FromSeconds(5);

    private readonly HttpFetcher _http = new(app.Settings.ConnectTimeout);
    private CancellationTokenSource? _feeds;
    private bool? _browserLinkOn;
    private Task _browserLinkWork = Task.CompletedTask;

    public void Start()
    {
        app.Queue.Changed += OnQueueChanged;
        app.SettingsChanged += Follow;
        Follow();
    }

    public void Dispose()
    {
        app.Queue.Changed -= OnQueueChanged;
        app.SettingsChanged -= Follow;
        _feeds?.Cancel();
        _feeds?.Dispose();
        _feeds = null;
        _http.Dispose();
    }

    /// <summary>A batch of downloads finished: send word of it, wherever that is set up.</summary>
    public async void OnBatchFinished(IReadOnlyList<QueueItem> items)
    {
        var settings = app.Settings;
        if (!NotificationsPlugin.Plugin.IsOn(settings))
            return;
        var finished = items.Where(item => item.State is QueueState.Done or QueueState.Failed).ToList();
        if (finished.Count == 0)
            return; // only files that were there already
        var done = finished.Count(item => item.State == QueueState.Done);
        var failures = await NotificationsPlugin.SendAsync(new PluginContext(_http, settings, app.Secrets), done, finished.Count - done,
                                                           finished.Select(item => item.DisplayName), CancellationToken.None);
        foreach (var failure in failures)
            CrashLog.Write("sending a notification", failure);
    }

    private void Follow()
    {
        var settings = app.Settings;
        if (FeedsPlugin.Plugin.IsOn(settings) != _feeds is not null)
            FollowFeeds(FeedsPlugin.Plugin.IsOn(settings));
        var browserLink = BrowserLinkPlugin.Plugin.IsOn(settings);
        if (browserLink != _browserLinkOn)
        {
            _browserLinkOn = browserLink;
            _browserLinkWork = FollowBrowserLinkAsync(_browserLinkWork, browserLink);
        }
    }

    private void FollowFeeds(bool on)
    {
        _feeds?.Cancel();
        _feeds?.Dispose();
        _feeds = on ? new CancellationTokenSource() : null;
        if (_feeds is { } run)
            _ = WatchFeedsAsync(run.Token);
    }

    private async Task WatchFeedsAsync(CancellationToken cancel)
    {
        var watcher = new FeedWatcher(_http, Path.Combine(app.ConfigFolder, "feeds-seen.txt"), links => AddFromFeedAsync(links, cancel),
                                      (what, e) => CrashLog.Write(what, e));
        try
        {
            await watcher.RunAsync(() => app.Settings, cancel);
        }
        catch (OperationCanceledException)
        {
            // turned off
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("keeping what the feeds had", e);
            notify("Feeds stopped", "What they had couldn't be kept. Turning the plugin off and on starts them again.");
        }
    }

    /// <summary>
    /// What a feed offers waits for the queue while the server is out of reach: it counts as seen already. Feeds are read
    /// where the app runs, in server mode too: a feed is the user's own list of links, not a site's file, and the agent has
    /// no feeds plugin.
    /// </summary>
    private async Task AddFromFeedAsync(IReadOnlyList<string> links, CancellationToken cancel)
    {
        while (!app.Queue.IsReady)
            await Task.Delay(QueueWait, cancel);
        var added = await app.Queue.Queue!.AddAsync(links);
        foreach (var rejected in added.Rejected)
            CrashLog.Write($"adding from a feed: {rejected.Reason}", new StageException(Stage.Link, rejected.Reason));
    }

    /// <summary>Files and the registry, off the UI thread, after what came before: an install and an uninstall must not cross.</summary>
    private async Task FollowBrowserLinkAsync(Task before, bool on)
    {
        await before;
        try
        {
            await Task.Run(() =>
            {
                if (on)
                    BrowserLinkPlugin.Install(Path.Combine(app.ConfigFolder, "browser-link"), Path.Combine(AppContext.BaseDirectory, "cli", "ferry.exe"));
                else
                    BrowserLinkPlugin.Uninstall();
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CrashLog.Write("setting up Browser Link", e);
            notify("Browser Link", $"It couldn't be set up for your browsers: {e.Message}");
        }
    }

    private void OnQueueChanged(QueueEvent e)
    {
        if (e is QueueItemStateChanged { Item: { State: QueueState.Done, Path: { } path }, Was: not QueueState.Done } && File.Exists(path) && AfterDownloadPlugin.Plugin.IsOn(app.Settings))
            _ = WorkOnAsync(path);
    }

    private async Task WorkOnAsync(string path)
    {
        var settings = app.Settings;
        var result = await Task.Run(() => FinishedFileWork.RunAsync(path, settings.Boolean(AfterDownloadPlugin.Scan), settings.Boolean(AfterDownloadPlugin.Extract),
                                                                    settings.Boolean(AfterDownloadPlugin.DeleteArchive), settings.Text(AfterDownloadPlugin.SevenZip), CancellationToken.None));
        if (result is not null)
            notify(result.Title, result.Message);
    }
}
