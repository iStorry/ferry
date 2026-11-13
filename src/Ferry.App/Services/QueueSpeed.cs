using Ferry.Core.Queue;
using Ferry.Core.Text;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace Ferry.App.Services;

/// <summary>
/// The queue's speed, measured once a second while Ferry runs, for the Downloads page's graph: how fast files
/// download (here, or on the server in server mode) and, in server mode, how fast they come here.
/// </summary>
public sealed class QueueSpeed
{
    /// <summary>How many seconds the graph shows.</summary>
    public const int Seconds = 60;

    // a graph's highest speed reaches this share of its height
    private const double GraphHeadroom = 0.85;

    private readonly QueueHost _queue;
    private readonly DispatcherQueueTimer _timer;

    public QueueSpeed(QueueHost queue, DispatcherQueue ui)
    {
        _queue = queue;
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Sample();
        // another queue (the mode changed, or the server connected) is another story
        queue.QueueReplaced += () =>
        {
            Downloading.Clear();
            ComingOver.Clear();
        };
    }

    public SpeedHistory Downloading { get; } = new(Seconds);

    public SpeedHistory ComingOver { get; } = new(Seconds);

    /// <summary>A second's speeds were added; on the UI thread.</summary>
    public event Action? Sampled;

    public void Start() => _timer.Start();

    /// <summary>"48.2 MB/s" as its figure and its unit, to show apart.</summary>
    public static (string Figure, string Unit) Parts(double bytesPerSecond)
    {
        var text = Units.Speed(bytesPerSecond);
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..]);
    }

    /// <summary>Where a graph has these speeds, newest on the right, against <paramref name="peak"/>.</summary>
    public static List<Point> Points(SpeedHistory history, double width, double height, double peak)
    {
        var points = new List<Point>();
        var step = width / (Seconds - 1);
        var x = width - step * (history.Samples.Count - 1);
        foreach (var sample in history.Samples)
        {
            points.Add(new Point(x, height - sample / peak * height * GraphHeadroom));
            x += step;
        }
        return points;
    }

    private void Sample()
    {
        var items = _queue.Queue?.Items ?? [];
        Downloading.Add(Sum(items, QueueState.Downloading));
        ComingOver.Add(Sum(items, QueueState.Transferring));
        Sampled?.Invoke();
    }

    private static double Sum(IReadOnlyList<QueueItem> items, QueueState state) =>
        items.Where(i => i.State == state && double.IsFinite(i.BytesPerSecond)).Sum(i => i.BytesPerSecond);
}
