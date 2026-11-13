namespace Ferry.Core.Queue;

/// <summary>The hours downloads may run, each day; <see cref="Stop"/> before <see cref="Start"/> runs past midnight.</summary>
public sealed record DownloadSchedule(TimeOnly Start, TimeOnly Stop)
{
    /// <summary>Whether downloads may run at <paramref name="now"/>; the same start and stop mean always.</summary>
    public bool IsOpen(TimeOnly now) =>
        Start == Stop || (Start < Stop ? now >= Start && now < Stop : now >= Start || now < Stop);
}
