namespace Ferry.Core.Http;

/// <summary>The one retry loop: every network step of getting a file runs through it.</summary>
public static class Retry
{
    /// <summary>
    /// Run <paramref name="attempt"/> until it succeeds, fails in a way that won't change, or runs out of tries.
    /// Each try is cancelled after its <see cref="RetryPolicy.TimeoutFor"/>; that counts as a retryable failure.
    /// </summary>
    /// <param name="onRetry">told about each retryable failure that will be tried again, with the try's number</param>
    /// <param name="progress">how far the work has got (bytes): a try that moved it on starts the count of tries
    /// afresh, so a download that keeps going on a flaky connection does not run out of tries</param>
    /// <exception cref="StageException">the last failure, saying how many tries were made</exception>
    public static async Task<T> RunAsync<T>(Stage stage, RetryPolicy policy, Func<CancellationToken, Task<T>> attempt,
                                            CancellationToken cancel, Action<StageException, int>? onRetry = null,
                                            Func<long>? progress = null)
    {
        var streak = 0;
        for (var number = 1; ; number++)
        {
            streak++;
            var timeout = policy.TimeoutFor(streak);
            var before = progress?.Invoke();
            StageException failure;
            using (var tryCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                tryCancel.CancelAfter(timeout);
                try
                {
                    return await attempt(tryCancel.Token);
                }
                catch (OperationCanceledException e) when (!cancel.IsCancellationRequested)
                {
                    // the try's own time ran out, or something inside it gave up on a wait of its own
                    var reason = timeout == Timeout.InfiniteTimeSpan ? "The try was cut short." : $"No answer within {timeout.TotalSeconds:0.#} s.";
                    failure = new StageException(stage, reason, isRetryable: true, inner: e);
                }
                catch (StageException e)
                {
                    failure = e;
                }
            }
            if (!failure.IsRetryable)
                throw failure;
            if (before is { } start && progress?.Invoke() > start)
                streak = 1;
            if (streak >= policy.MaxAttempts)
            {
                throw new StageException(stage, $"{failure.Message} Gave up after {number} tries.", isRetryable: true,
                                         inner: failure, status: failure.Status);
            }
            onRetry?.Invoke(failure, number);
            await Task.Delay(policy.DelayAfter(streak, failure.RetryAfter, failure.IsBusy), cancel);
        }
    }
}
