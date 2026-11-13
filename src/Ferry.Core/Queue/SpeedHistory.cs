namespace Ferry.Core.Queue;

/// <summary>The last few speeds measured, oldest first, for the Downloads page's graph.</summary>
public sealed class SpeedHistory(int capacity)
{
    private readonly Queue<double> _samples = new(capacity);

    public IReadOnlyCollection<double> Samples => _samples;

    /// <summary>The highest speed kept; 0 when there is none.</summary>
    public double Peak => _samples.Count == 0 ? 0 : _samples.Max();

    /// <summary>Keep <paramref name="bytesPerSecond"/>; the oldest goes once there are <c>capacity</c>.</summary>
    public void Add(double bytesPerSecond)
    {
        if (_samples.Count == capacity)
            _samples.Dequeue();
        _samples.Enqueue(double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0);
    }

    public void Clear() => _samples.Clear();
}
