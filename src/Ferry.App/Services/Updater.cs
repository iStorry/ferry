using System.Diagnostics;
using System.Globalization;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Settings;
using Ferry.Core.Updates;
using Microsoft.UI.Dispatching;

namespace Ferry.App.Services;

public enum UpdateState
{
    Unknown,
    Checking,
    UpToDate,
    NoRelease,
    Available,
    Downloading,
    Failed,
}

/// <summary>
/// New versions from GitHub: checked once a day (when Settings allow), shown on About, and installed with the
/// release's setup, checked against SHA256SUMS.txt first. A portable copy (no uninstaller beside it) is sent to the
/// release page instead.
/// </summary>
public sealed class Updater(AppServices app, DispatcherQueue ui) : IDisposable
{
    private const string LastCheck = "lastUpdateCheck";
    private const string Skipped = "skippedVersion";
    private static readonly TimeSpan Daily = TimeSpan.FromDays(1);
    private static readonly TimeSpan WaitAfterStart = TimeSpan.FromSeconds(10);
    private readonly HttpFetcher _http = new();

    public UpdateState State { get; private set; }

    /// <summary>The newer release, when <see cref="State"/> is <see cref="UpdateState.Available"/>.</summary>
    public Release? Available { get; private set; }

    public string Message { get; private set; } = "";

    /// <summary>0 to 1 while downloading.</summary>
    public double Progress { get; private set; }

    public DateTimeOffset? CheckedAt =>
        DateTimeOffset.TryParse(app.State(LastCheck), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    /// <summary>Installed by the setup (so an update can run it), not a folder copied somewhere.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>Raised on the UI thread when anything above changed.</summary>
    public event Action? Changed;

    /// <summary>Check in the background if Settings allow and the last check is a day old. Called on the UI thread.</summary>
    public void CheckDailyInBackground()
    {
        if (!app.Settings.Boolean(SettingKey.CheckUpdates) || CheckedAt is { } at && DateTimeOffset.UtcNow - at < Daily)
            return;
        _ = CheckAfterStartAsync();
    }

    private async Task CheckAfterStartAsync()
    {
        await Task.Delay(WaitAfterStart);
        await CheckAsync(manual: false);
    }

    /// <param name="manual">asked for on About: a skipped version is shown again</param>
    public async Task CheckAsync(bool manual)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading)
            return;
        Set(UpdateState.Checking, "");
        try
        {
            var release = await Task.Run(() => new Releases(_http).LatestAsync(CancellationToken.None));
            app.SetState(LastCheck, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            if (release is null)
            {
                Set(UpdateState.NoRelease, "No release is published yet.");
            }
            else if (release.IsNewerThan(Product.Version) && (manual || app.State(Skipped) != release.Version.ToString(3)))
            {
                Available = release;
                Set(UpdateState.Available, FirstLine(release.Notes));
            }
            else
            {
                Set(UpdateState.UpToDate, "");
            }
        }
        catch (StageException e)
        {
            Set(UpdateState.Failed, e.Message);
        }
#pragma warning disable CA1031 // whatever went wrong, the check must end (not stay "Checking" until a restart)
        catch (Exception e)
#pragma warning restore CA1031
        {
            CrashLog.Write("checking for updates", e);
            Set(UpdateState.Failed, e.Message);
        }
    }

    /// <summary>Don't offer this version again (a later one is offered as usual).</summary>
    public void Skip()
    {
        if (Available is { } release)
            app.SetState(Skipped, release.Version.ToString(3));
        Available = null;
        Set(UpdateState.UpToDate, "");
    }

    /// <summary>
    /// Download the setup and check it; true when it is ready to run. The caller then pauses the queue and quits, and
    /// <see cref="RunInstaller"/> starts it.
    /// </summary>
    public async Task<string?> DownloadAsync()
    {
        if (Available is not { } release)
            return null;
        var name = $"Ferry-{release.Version.ToString(3)}-setup.exe";
        if (release.Asset(name) is not { } asset)
        {
            Set(UpdateState.Failed, $"Release {release.Tag} has no {name}.");
            return null;
        }
        Set(UpdateState.Downloading, "");
        var progress = new Progress<double>(fraction =>
        {
            Progress = fraction;
            Changed?.Invoke();
        });
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "Ferry-update");
            var path = await Task.Run(() => new Releases(_http).DownloadAsync(release, asset, folder, CancellationToken.None, progress));
            Set(UpdateState.Available, FirstLine(release.Notes));
            return path;
        }
#pragma warning disable CA1031 // whatever went wrong, the download must end (not stay "Downloading")
        catch (Exception e)
#pragma warning restore CA1031
        {
            if (e is not (StageException or IOException or UnauthorizedAccessException))
                CrashLog.Write("downloading an update", e);
            Set(UpdateState.Failed, e.Message);
            return null;
        }
    }

    /// <summary>Run the setup quietly; it waits for Ferry to close, replaces it, and starts it again.</summary>
    public static void RunInstaller(string setup) =>
        Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true })?.Dispose();

    public void Dispose() => _http.Dispose();

    private void Set(UpdateState state, string message)
    {
        (State, Message) = (state, message);
        if (ui.HasThreadAccess)
            Changed?.Invoke();
        else
            ui.TryEnqueue(() => Changed?.Invoke());
    }

    private static string FirstLine(string notes) =>
        notes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .FirstOrDefault(l => !l.StartsWith('#')) ?? "";
}
