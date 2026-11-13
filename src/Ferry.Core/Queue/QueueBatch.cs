namespace Ferry.Core.Queue;

/// <summary>
/// When a batch of downloads has finished, from the queue's events. The queue going quiet isn't enough: a pause, a
/// cancel, a removal or a dropped connection does that too. It counts only when a file ended (done, failed or skipped)
/// since the batch began, and what quieted the queue wasn't a pause: the person stopped it there, and may come back to
/// it, so the batch carries on with what had ended. Events can arrive out of order, so older news is passed over.
/// </summary>
public sealed class QueueBatch
{
    private readonly HashSet<Guid> _busy = [];
    private readonly Dictionary<Guid, QueueItem> _ended = [];
    private readonly Dictionary<Guid, long> _latest = [];
    private readonly HashSet<Guid> _removed = [];

    /// <summary>Whether a file is in hand or in line, so the batch isn't over.</summary>
    public static bool IsBusy(QueueItem item) => item.IsActive || item.State is QueueState.Waiting or QueueState.OnServer;

    /// <summary>Start over with a queue as it is now: what it has done before isn't this batch's.</summary>
    public void Reset(IEnumerable<QueueItem> items)
    {
        _ended.Clear();
        _latest.Clear();
        _removed.Clear();
        _busy.Clear();
        _busy.UnionWith(items.Where(IsBusy).Select(item => item.Id));
    }

    /// <summary>Take in what happened.</summary>
    /// <returns>the files that ended in the batch, when this finished it; otherwise null</returns>
    public IReadOnlyList<QueueItem>? Note(QueueEvent change)
    {
        var item = change.Item;
        if (IsOlderNews(item))
            return null;
        if (change is QueueItemRemoved)
            _removed.Add(item.Id);
        _latest[item.Id] = item.Revision;
        var wasBusy = _busy.Count > 0;
        if (change is not QueueItemRemoved && IsBusy(item))
            _busy.Add(item.Id);
        else
            _busy.Remove(item.Id);
        if (change is QueueItemRemoved)
        {
            _ended.Remove(item.Id);
        }
        else if (change is QueueItemStateChanged)
        {
            if (item.State is QueueState.Done or QueueState.Failed or QueueState.Skipped)
                _ended[item.Id] = item;
            else
                _ended.Remove(item.Id); // tried again
        }
        if (!wasBusy || _busy.Count > 0 || item.State == QueueState.Paused)
            return null;
        var finished = _ended.Count > 0 ? _ended.Values.ToList() : null;
        _ended.Clear();
        return finished;
    }

    private bool IsOlderNews(QueueItem item) =>
        _removed.Contains(item.Id) || (_latest.TryGetValue(item.Id, out var latest) && latest > item.Revision);
}
