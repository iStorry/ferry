using Ferry.Core.Queue;

namespace Ferry.Core.Tests;

/// <summary>Waiting for a queue to reach a state, by its events, without sleeping.</summary>
internal static class QueueWait
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>Returns once <paramref name="condition"/> holds, checked now and after each of the queue's events.</summary>
    public static async Task UntilAsync(IDownloadQueue queue, Func<bool> condition)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(QueueEvent e)
        {
            if (condition())
                reached.TrySetResult();
        }
        queue.Changed += Watch;
        try
        {
            if (condition())
                return;
            await reached.Task.WaitAsync(Patience);
        }
        finally
        {
            queue.Changed -= Watch;
        }
    }
}
