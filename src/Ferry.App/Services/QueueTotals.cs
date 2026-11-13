using Ferry.Core.Queue;

namespace Ferry.App.Services;

/// <summary>How far the queue as a whole has got, for the ring in the sidebar and the taskbar button.</summary>
public static class QueueTotals
{
    /// <summary>
    /// From 0 to 1, by size. In server mode a file counts half for the server's download and half for bringing it over,
    /// so the bar only moves forward when a file finished on the server starts coming over.
    /// </summary>
    public static double Fraction(IReadOnlyCollection<QueueItem> items, bool serverMode)
    {
        var counted = items.Where(i => i.Total is > 0 && i.State is not (QueueState.Skipped or QueueState.Failed or QueueState.Cancelled)).ToList();
        var total = counted.Sum(i => (double)i.Total!.Value);
        return total > 0 ? counted.Sum(i => Done(i, serverMode) * i.Total!.Value) / total : 0;
    }

    private static double Done(QueueItem item, bool serverMode)
    {
        if (item.State == QueueState.Done)
            return 1;
        var part = Math.Clamp((double)item.Done / item.Total!.Value, 0, 1);
        if (!serverMode)
            return part;
        return item.State switch
        {
            QueueState.OnServer => 0.5,
            QueueState.Transferring => 0.5 + part / 2,
            _ => part / 2,
        };
    }
}
