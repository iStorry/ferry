namespace Ferry.Core.Download;

/// <summary>
/// Something a download did that a front end may show, as data. Raised on a background thread; nothing should
/// parse the messages.
/// </summary>
public abstract record DownloadEvent;

/// <param name="Path">the file's final path; until it is complete the bytes go to <see cref="PartFile.PartPath"/></param>
/// <param name="Total">the file's size, when the server says it</param>
/// <param name="Done">bytes already on disk from an earlier attempt</param>
/// <param name="Resumed">carrying on from an earlier, interrupted download</param>
public sealed record DownloadStarted(string Path, long? Total, long Done, int Connections, bool Resumed) : DownloadEvent;

/// <param name="BytesPerSecond">smoothed over the last few seconds</param>
/// <param name="Connections">connections receiving data right now</param>
public sealed record DownloadProgressed(long Done, long? Total, double BytesPerSecond, int Connections) : DownloadEvent;

/// <summary>A connection failed and is tried again; the download carries on.</summary>
/// <param name="Try">the number of the try that failed</param>
public sealed record DownloadRetrying(string Reason, int Try) : DownloadEvent
{
    /// <summary>What to give <see cref="Http.Retry.RunAsync"/> so that each retry is reported to <paramref name="onEvent"/>.</summary>
    internal static Action<StageException, int> Reporter(Action<DownloadEvent> onEvent) =>
        (failure, number) => onEvent(new DownloadRetrying(failure.Message, number));
}

/// <summary>The server called a connection busy (503, 429), so it stopped and left its part to the others.</summary>
/// <param name="Left">connections the download goes on with</param>
/// <param name="Reason">what the server answered</param>
public sealed record DownloadConnectionsReduced(int Left, string Reason) : DownloadEvent;

/// <summary>The download link had expired or was refused, and a new one was fetched from the file's page.</summary>
public sealed record DownloadLinkRenewed(DateTimeOffset? ExpiresAt) : DownloadEvent;

/// <summary>What was downloaded before cannot be continued (the file changed on the server); starting from the beginning.</summary>
public sealed record DownloadRestarted(string Reason) : DownloadEvent;

/// <summary>The file is complete under its final name.</summary>
public sealed record DownloadFinished(string Path, long Bytes) : DownloadEvent;

/// <summary>A file of that name was already there and <see cref="ExistingFile.Skip"/> was chosen.</summary>
public sealed record DownloadSkipped(string Path) : DownloadEvent;
