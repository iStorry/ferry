using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Ferry.App.Services;

/// <summary>
/// The design's colours code needs to pick (a row's state), for a given theme. {ThemeResource} isn't available to code, and
/// looking the keys up in Application resources answers for the app's startup theme rather than a theme pinned in Settings
/// since, so the values (those of Styles.xaml) live here too; the accent comes from <see cref="Accent"/>. In high contrast the
/// system's colours decide instead: no theme is pinned then, so the resources are the right answer.
/// </summary>
public static class ThemeBrushes
{
    public const string Accent = "AccentTextFillColorPrimaryBrush";
    public const string Success = "SystemFillColorSuccessBrush";
    public const string Caution = "SystemFillColorCautionBrush";
    public const string Critical = "SystemFillColorCriticalBrush";
    public const string Primary = "TextFillColorPrimaryBrush";
    public const string Secondary = "TextFillColorSecondaryBrush";
    public const string Tertiary = "TextFillColorTertiaryBrush";
    public const string Subtle = "SubtleFillColorSecondaryBrush";
    public const string Card = "CardBackgroundFillColorDefaultBrush";
    public const string CardStroke = "DividerStrokeColorDefaultBrush";
    public const string Sunk = "FerrySunkBrush";

    private static readonly Dictionary<string, (Color Light, Color Dark)> Colors = new()
    {
        [Success] = (Color.FromArgb(0xFF, 0x1F, 0x7A, 0x4D), Color.FromArgb(0xFF, 0x72, 0xCF, 0x9B)),
        [Caution] = (Color.FromArgb(0xFF, 0xA1, 0x5F, 0x05), Color.FromArgb(0xFF, 0xE8, 0xB5, 0x4F)),
        [Critical] = (Color.FromArgb(0xFF, 0xC9, 0x36, 0x2A), Color.FromArgb(0xFF, 0xFF, 0x8A, 0x80)),
        [Primary] = (Color.FromArgb(0xFF, 0x12, 0x16, 0x1B), Color.FromArgb(0xFF, 0xF3, 0xF5, 0xF7)),
        [Secondary] = (Color.FromArgb(0xFF, 0x57, 0x60, 0x6B), Color.FromArgb(0xFF, 0xA9, 0xB2, 0xBD)),
        [Tertiary] = (Color.FromArgb(0xFF, 0x6B, 0x74, 0x80), Color.FromArgb(0xFF, 0x80, 0x8A, 0x96)),
        [Subtle] = (Color.FromArgb(0x0B, 0x10, 0x14, 0x1A), Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
        [Card] = (Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xFF, 0x1A, 0x1F, 0x25)),
        [CardStroke] = (Color.FromArgb(0x16, 0x10, 0x14, 0x1A), Color.FromArgb(0x13, 0xFF, 0xFF, 0xFF)),
        [Sunk] = (Color.FromArgb(0x09, 0x10, 0x14, 0x1A), Color.FromArgb(0x33, 0x00, 0x00, 0x00)),
    };

    private static readonly Dictionary<(string, bool), SolidColorBrush> Cache = [];
    private static volatile bool _highContrast = new AccessibilitySettings().HighContrast;
    private static ThemeSettings? _system; // kept, as dropping it would end its events

    /// <summary>The system's high contrast went on or off, or to other colours: on the UI thread, for what is drawn in them to be drawn again.</summary>
    public static event Action? ContrastChanged;

    public static bool HighContrast => _highContrast;

    public static Brush Get(string key, ElementTheme theme)
    {
        if (_highContrast)
            return (Brush)Application.Current.Resources[key];
        var dark = theme == ElementTheme.Dark;
        if (Cache.TryGetValue((key, dark), out var cached))
            return cached;
        var color = key == Accent
            ? Services.Accent.Current(dark)
            : Colors.TryGetValue(key, out var pair) ? (dark ? pair.Dark : pair.Light) : Microsoft.UI.Colors.Gray;
        return Cache[(key, dark)] = new SolidColorBrush(color);
    }

    /// <summary>The accent setting changed: what was handed out before is the old colour.</summary>
    public static void AccentChanged()
    {
        Cache.Remove((Accent, false));
        Cache.Remove((Accent, true));
    }

    /// <summary>
    /// Follow the system's high contrast from now on, through a window of the app's: AccessibilitySettings has its event
    /// only for a UWP window. Without it the colours are right from the next start, so a failure here doesn't stop this one.
    /// </summary>
    public static void FollowHighContrast(WindowId window)
    {
        try
        {
            _system = ThemeSettings.CreateForWindowId(window);
            _system.Changed += (settings, _) =>
            {
                _highContrast = settings.HighContrast;
                App.Window?.DispatcherQueue.TryEnqueue(() => ContrastChanged?.Invoke());
            };
        }
        catch (COMException e)
        {
            CrashLog.Write("following high contrast", e);
        }
    }
}
