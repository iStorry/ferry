using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.App.Services;

/// <summary>The square behind a plugin's icon.</summary>
public static class PluginTile
{
    /// <summary>A plugin that is on has its tile in the accent; one that is off, plain.</summary>
    public static void Show(Border tile, FontIcon icon, bool on, ElementTheme theme)
    {
        var tone = ThemeBrushes.Get(on ? ThemeBrushes.Accent : ThemeBrushes.Secondary, theme);
        icon.Foreground = tone;
        tile.Background = on ? Pill.Wash(tone) : ThemeBrushes.Get(ThemeBrushes.Subtle, theme);
    }
}
