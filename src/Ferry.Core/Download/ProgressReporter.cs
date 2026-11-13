using System.Diagnostics;

namespace Ferry.Core.Download;

/// <summary>
/// Raises <see cref="DownloadProgressed"/> four times a second while a download runs, and saves its state every few
/// seconds so that it can continue after a crash. A save flushes the part to disk, which can take a while, so it
/// runs beside the timer rather than on it: progress keeps coming, and no two saves run at once.
/// </summary>
internal sealed class ProgressReporter : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(5);

    // weight of the newest measurement in the speed: about the last second counts most
    private const double Smoothing = 0.3;

    private readonly PeriodicTimer _timer = new(Interval);
    private readonly Func<long> _done;
    private readonly Func<long?> _total;
    private readonly Func<int> _connections;
    private readonly Action<DownloadEvent> _onEvent;
    private readonly Action? _save;
    private readonly Task _loop;
    private Task _saving = Task.CompletedTask;
    private double _speed;

    /// <param name="save">called every <see cref="SaveEvery"/>, alongside the download, never twice at once</param>
    public ProgressReporter(Func<long> done, long? total, Func<int> connections, Action<DownloadEvent> onEvent, Action? save)
        : this(done, () => total, connections, onEvent, save)
    {
    }

    /// <param name="total">the size as it is known so far, for a file that only says once it has started</param>
    /// <param name="save">called every <see cref="SaveEvery"/>, alongside the download, never twice at once</param>
    public ProgressReporter(Func<long> done, Func<long?> total, Func<int> connections, Action<DownloadEvent> onEvent, Action? save)
    {
        (_done, _total, _connections, _onEvent, _save) = (done, total, connections, onEvent, save);
        _loop = RunAsync();
    }

    private async Task RunAsync()
    {
        var clock = Stopwatch.StartNew();
        var (last, lastAt, lastSaved, first) = (_done(), TimeSpan.Zero, TimeSpan.Zero, true);
        while (await _timer.WaitForNextTickAsync())
        {
            var (now, done) = (clock.Elapsed, _done());
            // a try that starts over (one connection, no ranges) goes back to 0: that's no speed, not a negative one
            var instant = Math.Max(0, (done - last) / (now - lastAt).TotalSeconds);
            _speed = first ? instant : _speed + Smoothing * (instant - _speed);
            (last, lastAt, first) = (done, now, false);
            _onEvent(new DownloadProgressed(done, _total(), _speed, _connections()));
            if (_save is { } save && now - lastSaved >= SaveEvery && _saving.IsCompleted)
            {
                await _saving; // a save that failed with more than a held file is the download's failure
                lastSaved = now;
                _saving = Task.Run(() => SaveOnce(save));
            }
        }
    }

    private static void SaveOnce(Action save)
    {
        try
        {
            save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the state file is held for a moment (an antivirus, a sync tool): saved again next time
        }
    }

    /// <summary>Stop, wait for a save still running, and report where the download got to.</summary>
    public async ValueTask DisposeAsync()
    {
        _timer.Dispose();
        await _loop;
        await _saving;
        _onEvent(new DownloadProgressed(_done(), _total(), _speed, 0));
    }
}
