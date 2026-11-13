using Microsoft.UI.Dispatching;

namespace Ferry.App.Services;

/// <summary>Work asked for more often than it is worth doing: done at once, then at most once per <see cref="Every"/>, ending with the last asking.</summary>
public sealed class Throttle
{
    /// <summary>What follows the queue reaches the screen no more often than this.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMilliseconds(250);

    private readonly Action _work;
    private readonly DispatcherQueueTimer _timer;
    private bool _asked;

    public Throttle(DispatcherQueue ui, Action work)
    {
        _work = work;
        _timer = ui.CreateTimer();
        _timer.Interval = Every;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) =>
        {
            if (_asked)
                Run();
        };
    }

    public void Ask()
    {
        if (_timer.IsRunning)
            _asked = true;
        else
            Run();
    }

    /// <summary>What was asked for and not done yet stays undone.</summary>
    public void Stop()
    {
        _timer.Stop();
        _asked = false;
    }

    private void Run()
    {
        _asked = false;
        _timer.Start();
        _work();
    }
}
