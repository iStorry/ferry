using System.Diagnostics;

namespace Ferry.App.Services;

/// <summary>
/// The room on a folder's drive, for what shows it. Read off the UI thread, as a share that has gone away takes seconds to
/// say so, one read at a time, and kept for a few seconds. Used from the UI thread.
/// </summary>
public sealed class DriveRoom
{
    private static readonly TimeSpan KeptFor = TimeSpan.FromSeconds(5);

    private string? _folder;
    private long _readAt;
    private bool _reading;

    /// <summary>Bytes free, or null when the drive won't say (or hasn't yet).</summary>
    public long? Free { get; private set; }

    /// <summary>How full it is, from 0 to 1, or null when the drive won't say (or hasn't yet).</summary>
    public double? Used { get; private set; }

    /// <summary>A read found something else than before.</summary>
    public event Action? Changed;

    /// <summary>Have it read again, if what is known is more than a few seconds old or of another folder.</summary>
    public void Refresh(string folder)
    {
        var moved = folder != _folder;
        _folder = folder;
        if (!_reading && (moved || Stopwatch.GetElapsedTime(_readAt) >= KeptFor))
            _ = ReadAsync(folder);
    }

    private async Task ReadAsync(string folder)
    {
        _reading = true;
        var (free, used) = await Task.Run(() => Read(folder));
        _reading = false;
        _readAt = Stopwatch.GetTimestamp();
        if (_folder != folder)
        {
            _ = ReadAsync(_folder!);
            return;
        }
        if (free == Free)
            return;
        (Free, Used) = (free, used);
        Changed?.Invoke();
    }

    private static (long? Free, double? Used) Read(string folder)
    {
        try
        {
            var drive = new DriveInfo(folder);
            var free = drive.AvailableFreeSpace;
            return (free, 1 - (double)free / drive.TotalSize);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (null, null); // a drive that's gone, or a share that won't say: nothing is said about it
        }
    }
}
