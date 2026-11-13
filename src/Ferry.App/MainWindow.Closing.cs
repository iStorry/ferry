using Ferry.App.Services;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Plugins.Server;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Ferry.App;

/// <summary>The window's end: a batch finishing and what was chosen for then, the notification area, and closing.</summary>
public sealed partial class MainWindow
{
    // how long the user has to stop what was chosen for when the queue finishes
    private const int WhenDoneSeconds = 60;
    private const string DidntWork = "That didn't work";

    private readonly QueueBatch _batch = new();
    private bool _closeConfirmed;
    private bool _closing;
    private bool _quitting;
    private TrayIcon? _tray;

    private void StartTray()
    {
        var tray = new TrayIcon(Handle, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        tray.Open += BringToFront;
        tray.PauseAll += () => _ = _app.Queue.Queue?.PauseAllAsync();
        tray.ResumeAll += () => _ = _app.Queue.Queue?.ResumeAllAsync();
        tray.Quit += Quit;
        Closed += (_, _) => tray.Dispose();
        _tray = tray;
    }

    /// <summary>Close for real, also when closing would otherwise keep running in the notification area.</summary>
    private void Quit()
    {
        _quitting = true;
        BringToFront(); // closing with files downloading asks first
        _ = Dialogs.GuardAsync(Root.XamlRoot, DidntWork, CloseAskingAsync);
    }

    /// <summary>A batch finished, perhaps while the window was behind others or hidden.</summary>
    /// <param name="items">the files that ended in it</param>
    private void OnBatchFinished(IReadOnlyList<QueueItem> items)
    {
        Taskbar.Flash();
        UpdateModeLine(); // what finished took up room
        _plugins?.OnBatchFinished(items);
        if (_app.Settings.Boolean(SettingKey.NotifyWhenDone) && !Taskbar.IsInFront)
            _tray?.Notify("Downloads finished", CountsOf(items));
        _ = Dialogs.GuardAsync(Root.XamlRoot, DidntWork, RunWhenDoneAsync);
    }

    private static string CountsOf(IReadOnlyList<QueueItem> items)
    {
        var done = items.Count(item => item.State == QueueState.Done);
        var failed = items.Count(item => item.State == QueueState.Failed);
        var skipped = items.Count(item => item.State == QueueState.Skipped);
        var parts = new[] { done > 0 ? $"{done} done" : null, failed > 0 ? $"{failed} failed" : null, skipped > 0 ? $"{skipped} skipped" : null };
        return string.Join(", ", parts.OfType<string>());
    }

    /// <summary>What was chosen for when the queue finishes: once, after a countdown the user can stop.</summary>
    private async Task RunWhenDoneAsync()
    {
        var action = _app.Queue.WhenDone;
        if (action == WhenDone.Nothing)
            return;
        _app.Queue.WhenDone = WhenDone.Nothing;
        BringToFront();
        var ranOut = await Dialogs.CountdownAsync(Root.XamlRoot, $"Downloads finished: {WhenDoneActions.InSentence(action)}?",
                                                  seconds => $"In {seconds} second{(seconds == 1 ? "" : "s")}, unless you stop it.", WhenDoneSeconds);
        if (!ranOut)
            return;
        if (action == WhenDone.Close)
        {
            await PauseToCloseAsync();
            Quit();
        }
        else if (!await RunOnPcAsync(action))
        {
            await Dialogs.ShowErrorAsync(Root.XamlRoot, DidntWork, $"Windows didn't {WhenDoneActions.InSentence(action)}.");
        }
    }

    /// <summary>
    /// Sleep or shut down. A server's connection stays through sleep, and comes back by itself after it; shutting down ends it,
    /// and if this PC is still here after that (Windows refused), it is made again.
    /// </summary>
    /// <returns>whether Windows took it</returns>
    private async Task<bool> RunOnPcAsync(WhenDone action)
    {
        var shuttingDown = action == WhenDone.ShutDown;
        try
        {
            await _app.Queue.PauseAndSaveAsync(disconnect: shuttingDown);
            return WhenDoneActions.RunOnPc(action);
        }
        finally
        {
            if (shuttingDown)
                await ReconnectAsync();
        }
    }

    /// <summary>The connection to the server again, after it was closed for something that didn't happen.</summary>
    private async Task ReconnectAsync()
    {
        if (_app.Settings.Mode == DownloadMode.Server)
            await _app.Queue.SwitchAsync();
    }

    /// <summary>Install an update: pause and save what runs, start the setup, and close so it can replace the app. If it can't start, the window comes back and says why.</summary>
    public async Task InstallUpdateAsync(string setup)
    {
        WindowPlacement.Save(AppWindow, _app);
        ShowClosing("Getting ready to update…", _app.Settings.Mode == DownloadMode.Server
            ? "Disconnecting from the server; it keeps downloading meanwhile."
            : "Pausing downloads; what was downloaded is kept.");
        try
        {
            await _app.Queue.PauseAndSaveAsync();
            Updater.RunInstaller(setup);
        }
#pragma warning disable CA1031 // whatever stopped it (the setup's permission prompt declined, say), the window must not stay covered
        catch (Exception e)
#pragma warning restore CA1031
        {
            CrashLog.Write("starting the update", e);
            HideClosing();
            await ReconnectAsync();
            await Dialogs.ShowErrorAsync(Root.XamlRoot, "The update didn't start", e.Message);
            return;
        }
        Taskbar.Show(Taskbar.State.None);
        _closeConfirmed = true;
        Close();
    }

    /// <summary>The window's close button and the like, which close only once this has decided to.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
            return;
        args.Cancel = true;
        await CloseAskingAsync();
    }

    /// <summary>
    /// Closing while this PC downloads asks first; server mode closes at once (the server carries on). Also what Quit calls: closing
    /// from code doesn't raise <see cref="AppWindow.Closing"/>, which is for the system's own ways of closing a window.
    /// </summary>
    private async Task CloseAskingAsync()
    {
        if (_closing)
            return; // already asking, or already pausing to close
        if (_tray is not null && !_quitting && _app.Settings.Boolean(SettingKey.CloseToTray))
        {
            WindowPlacement.Save(AppWindow, _app);
            AppWindow.Hide();
            return;
        }
        _closing = true;
        try
        {
            WindowPlacement.Save(AppWindow, _app);
            var running = _app.Queue.ActiveHere;
            if (_app.Settings.Mode == DownloadMode.Local && running > 0
                && !await Dialogs.ConfirmAsync(Root.XamlRoot, $"{running} download{(running == 1 ? " is" : "s are")} running",
                    running == 1
                        ? "Pausing keeps what was downloaded; it continues where it stopped next time you open Ferry."
                        : "Pausing keeps what was downloaded; they continue where they stopped next time you open Ferry.",
                    "Pause and quit", "Keep downloading", tone: DialogTone.Caution))
                return;
            if (_app.Settings.Mode == DownloadMode.Server)
                ShowClosing("Disconnecting from the server…", "The server keeps downloading; what it finishes comes over next time you open Ferry.");
            else if (running > 0)
                ShowClosing("Pausing downloads…", "What was downloaded is kept.");
            await PauseToCloseAsync();
            Taskbar.Show(Taskbar.State.None);
            _closeConfirmed = true;
            Close();
        }
        finally
        {
            if (!_closeConfirmed)
            {
                _closing = false; // kept open (or something failed): the next close asks again
                _quitting = false;
                HideClosing();
            }
        }
    }

    private async Task PauseToCloseAsync()
    {
        try
        {
            await _app.Queue.PauseAndSaveAsync();
        }
#pragma warning disable CA1031 // closing must close: what could not be paused or saved is logged
        catch (Exception e)
#pragma warning restore CA1031
        {
            CrashLog.Write("pausing to close", e);
        }
    }

    /// <summary>Cover the window with what Ferry is finishing before it closes; nothing can be clicked meanwhile.</summary>
    private void ShowClosing(string text, string detail)
    {
        (ClosingText.Text, ClosingDetail.Text) = (text, detail);
        ClosingRing.IsActive = true;
        ClosingOverlay.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(ClosingText)
            ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void HideClosing()
    {
        ClosingRing.IsActive = false;
        ClosingOverlay.Visibility = Visibility.Collapsed;
    }
}
