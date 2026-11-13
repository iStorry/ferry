using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ferry.App.Services;

/// <summary>A short fact as a pill (docs/mocks/ferry.css, .pill), half its height at the corners.</summary>
public static class Pill
{
    private const byte WashAlpha = 0x24;
    private const double SpinnerSize = 12;

    /// <param name="tone">a <see cref="ThemeBrushes"/> key for the colour, or null for a plain pill</param>
    /// <param name="glyph">an icon before the text, if any</param>
    public static Border Make(string text, ElementTheme theme, string? tone = null, string? glyph = null)
    {
        var foreground = ThemeBrushes.Get(tone ?? ThemeBrushes.Secondary, theme);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null)
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = foreground });
        content.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = foreground,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        var pill = Shaped(content);
        if (tone is not null)
            pill.Background = Wash(foreground);
        return pill;
    }

    /// <summary>A short fact about what is under way: a spinner before the text.</summary>
    public static Border Working(string text, ElementTheme theme)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new ProgressRing
        {
            Width = SpinnerSize,
            Height = SpinnerSize,
            MinWidth = 0,
            MinHeight = 0,
            IsActive = true,
            Foreground = ThemeBrushes.Get(ThemeBrushes.Accent, theme),
        });
        content.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionMutedStyle"], VerticalAlignment = VerticalAlignment.Center });
        return Shaped(content);
    }

    /// <summary>How the connection to the server stands.</summary>
    public static Border For(ModeStatus status, ElementTheme theme) => status switch
    {
        ModeStatus.Connected => Make("Connected", theme, ThemeBrushes.Accent, "\uE930"),
        ModeStatus.Connecting => Working("Connecting", theme),
        ModeStatus.Reconnecting => Working("Reconnecting", theme),
        ModeStatus.Disconnecting => Working("Disconnecting", theme),
        ModeStatus.Offline => Make("Not reachable", theme, ThemeBrushes.Critical, "\uEA39"),
        _ => Make("Not in use", theme),
    };

    /// <summary>A faint wash of a colour, for what sits behind something in that colour.</summary>
    public static SolidColorBrush Wash(Brush tone)
    {
        var color = ((SolidColorBrush)tone).Color;
        color.A = WashAlpha;
        return new SolidColorBrush(color);
    }

    private static Border Shaped(FrameworkElement content) => new()
    {
        Style = (Style)Application.Current.Resources["PillStyle"],
        Child = content,
    };
}
