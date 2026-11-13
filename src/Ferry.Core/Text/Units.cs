using System.Globalization;

namespace Ferry.Core.Text;

/// <summary>Sizes, speeds and times as people read them.</summary>
public static class Units
{
    public const long KiB = 1L << 10;
    public const long MiB = 1L << 20;
    public const long GiB = 1L << 30;

    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>"1.4 GB", "512 KB", "73 B" (powers of 1024, as the settings use).</summary>
    public static string Size(double bytes)
    {
        var unit = 0;
        while (bytes >= 1024 && unit < SizeUnits.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        var format = unit == 0 || bytes >= 100 ? "0" : "0.0";
        return $"{bytes.ToString(format, CultureInfo.InvariantCulture)} {SizeUnits[unit]}";
    }

    public static string Speed(double bytesPerSecond) => $"{Size(bytesPerSecond)}/s";

    // past this, "time left" says nothing useful (and a speed fading towards zero would overflow TimeSpan)
    private static readonly TimeSpan LongestTimeLeft = TimeSpan.FromDays(30);

    /// <summary>How long <paramref name="remaining"/> bytes take at <paramref name="bytesPerSecond"/>; null when the
    /// speed is too low (or not a number) for that to mean anything.</summary>
    public static TimeSpan? TimeLeft(double remaining, double bytesPerSecond)
    {
        if (!(bytesPerSecond >= 1) || !(remaining >= 0) || double.IsInfinity(remaining))
            return null;
        var seconds = remaining / bytesPerSecond;
        return seconds <= LongestTimeLeft.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>"42s", "3m 05s", "1h 20m".</summary>
    public static string Time(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes:00}m"
        : time.TotalMinutes >= 1 ? $"{time.Minutes}m {time.Seconds:00}s"
        : $"{Math.Max(0, (int)time.TotalSeconds)}s";

    public static string Percent(long done, long? total) =>
        total is > 0 ? $"{Math.Min(100, done * 100 / total.Value),3}%" : "  ?%";
}
