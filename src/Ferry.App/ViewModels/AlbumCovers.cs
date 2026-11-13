using Microsoft.UI.Xaml.Media;

namespace Ferry.App.ViewModels;

/// <summary>What an album shows until its picture does, or without one: a colour of its own, and its initial on it.</summary>
public static class AlbumCovers
{
    /// <summary>How wide a card's picture is decoded.</summary>
    public const int ThumbnailPixels = 400;

    // pairs to blend from top left to bottom right, light text on each in both themes
    private static readonly (uint From, uint To)[] Blends =
    [
        (0xFF4F5BD5, 0xFF8A4FD5), (0xFFD5534F, 0xFFD58A4F), (0xFF2A9D7A, 0xFF1F7A99), (0xFFC77B30, 0xFFC7A630),
        (0xFF3A8FD5, 0xFF2F5FC7), (0xFFC74F7A, 0xFF8F4FC7), (0xFF3F9D4F, 0xFF2A8F8A), (0xFF6E56CF, 0xFF3E7BD5),
    ];

    /// <summary>The first letter or digit of the name, upper case; "" if it has none.</summary>
    public static string Initial(string title) =>
        title.FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0' ? char.ToUpperInvariant(c).ToString() : "";

    /// <summary>The album's colour, the same for its slug each time.</summary>
    public static LinearGradientBrush Placeholder(string slug)
    {
        var hash = slug.Aggregate(17, (sum, c) => unchecked(sum * 31 + c));
        var (from, to) = Blends[(int)((uint)hash % Blends.Length)];
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops = { new GradientStop { Color = Argb(from), Offset = 0 }, new GradientStop { Color = Argb(to), Offset = 1 } },
        };
    }

    private static Windows.UI.Color Argb(uint argb) =>
        Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
