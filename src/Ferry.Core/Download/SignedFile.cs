using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Core.Download;

/// <summary>
/// The file's current download link. A new one is asked of the file's site when the server refuses the old one or
/// it is about to expire; all connections share it, so the page is asked once, not once per connection.
/// </summary>
internal sealed class SignedFile(SiteCatalog sites, ResolvedFile first, RequestDetails details, Action<DownloadEvent> onEvent) : IDisposable
{
    /// <summary>What it means when a link that gave the file now gives a page.</summary>
    public const string NowAWebPage = "The link now gives a web page instead of the file, perhaps a login that ran out. What was downloaded so far is kept; try again later.";

    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    // how long the site may take to give a new link, however many steps and retries that is for it
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private ResolvedFile _current = first;

    // a new link that the server has not accepted yet: if it refuses that one too, a third won't help
    private bool _untested;

    private bool Untested
    {
        get => Volatile.Read(ref _untested);
        set => Volatile.Write(ref _untested, value);
    }

    public ResolvedFile Current => Volatile.Read(ref _current);

    /// <summary>What to send with every request for this file.</summary>
    public RequestDetails Details => details;

    /// <summary>The link to use for the next request: a fresh one if the current is about to expire.</summary>
    public async Task<ResolvedFile> NextAsync(CancellationToken cancel)
    {
        var current = Current;
        return current.ExpiresAt is { } expires && expires - DateTimeOffset.UtcNow < ExpiryMargin && !Untested
            ? await RefreshAsync(current, cancel)
            : current;
    }

    /// <summary>The server answered a request made with <paramref name="file"/>.</summary>
    public void Accepted(ResolvedFile file)
    {
        if (ReferenceEquals(file, Current))
            Untested = false;
    }

    /// <summary>The server refused <paramref name="refused"/>: fetch a new link, unless another connection already did.</summary>
    /// <exception cref="StageException">the new link was refused too, or the link now has another file or a web page</exception>
    public async Task<ResolvedFile> RefreshAsync(ResolvedFile refused, CancellationToken cancel)
    {
        await _refreshing.WaitAsync(cancel);
        try
        {
            if (!ReferenceEquals(refused, _current))
                return _current;
            if (Untested)
                throw new StageException(Stage.Download, "The server refuses the file even with a new download link.");
            var fresh = await ResolveAsync(refused.Link, cancel);
            if (fresh.FileId != refused.FileId)
                throw new StageException(Stage.Download, "The link now leads to a different file.");
            if (fresh.IsWebPage && !refused.IsWebPage)
                throw new StageException(Stage.Download, NowAWebPage);
            Untested = true;
            Volatile.Write(ref _current, fresh);
            onEvent(new DownloadLinkRenewed(fresh.ExpiresAt));
            return fresh;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    public void Dispose() => _refreshing.Dispose();

    private async Task<ResolvedFile> ResolveAsync(FileLink link, CancellationToken cancel)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        budget.CancelAfter(ResolveBudget);
        try
        {
            return await sites.ResolveAsync(link, details, budget.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new StageException(Stage.Download, $"The site gave no new download link within {ResolveBudget.TotalMinutes:0} minutes.", isRetryable: true);
        }
    }
}
