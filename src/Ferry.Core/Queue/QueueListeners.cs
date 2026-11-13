namespace Ferry.Core.Queue;

/// <summary>
/// How every queue tells its listeners of a change: one that fails (a full disk under "ferry get > file", a fault in
/// the UI) is reported and the queue goes on, as a listener is never a reason for a download to stop.
/// </summary>
public static class QueueListeners
{
    /// <param name="changed">the queue's listeners</param>
    /// <param name="log">where a listener's failure goes; nowhere when null</param>
    public static void Raise(Action<QueueEvent>? changed, IEnumerable<QueueEvent> events, Action<string, Exception>? log)
    {
        if (changed is null)
            return;
        foreach (var e in events)
        {
            try
            {
                changed(e);
            }
#pragma warning disable CA1031 // whatever a listener throws, the queue carries on
            catch (Exception failure)
#pragma warning restore CA1031
            {
                log?.Invoke($"a queue listener failed on {e.GetType().Name}", failure);
            }
        }
    }
}
