using Ferry.Core.Settings;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Ferry.App.Services;

/// <summary>
/// The colour for what is moving (the Accent setting). WinUI takes its accent colours from these resources: the dark theme
/// reads the Light shades and the light theme the Dark ones, so each shade only needs the colour for its theme.
/// </summary>
public static class Accent
{
    /// <summary>The Accent setting's value that follows Windows.</summary>
    public const string AsWindows = "Windows";

    private static readonly Dictionary<string, (Color OnLight, Color OnDark)> Colors = new(StringComparer.Ordinal)
    {
        ["Tide"] = (Color.FromArgb(0xFF, 0x0A, 0x7F, 0x72), Color.FromArgb(0xFF, 0x4F, 0xDC, 0xC4)),
        ["Signal"] = (Color.FromArgb(0xFF, 0xC9, 0x48, 0x0F), Color.FromArgb(0xFF, 0xFF, 0x8C, 0x52)),
        ["Iris"] = (Color.FromArgb(0xFF, 0x5B, 0x57, 0xD9), Color.FromArgb(0xFF, 0xA3, 0xA1, 0xFF)),
    };

    private static readonly string[] LightShades = ["SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3"];
    private static readonly string[] DarkShades = ["SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];
    private static readonly UISettings Windows = new();

    private static string _current = "";

    /// <summary>What to show for a choice, and what it is saved as.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Colors.Keys, AsWindows];

    /// <summary>The colour itself, in a theme.</summary>
    public static Color ColorOf(string name, bool dark) => Colors.TryGetValue(name, out var pair)
        ? dark ? pair.OnDark : pair.OnLight
        : Windows.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);

    /// <summary>The colour in use, in a theme.</summary>
    public static Color Current(bool dark) => ColorOf(_current, dark);

    /// <summary>Use the choice in the settings; true when that changed what is in use, which the window has to draw again.</summary>
    public static bool Apply(FerrySettings settings)
    {
        var name = settings.Text(SettingKey.Accent);
        if (name == _current)
            return false;
        var first = _current.Length == 0;
        _current = name;
        var resources = Application.Current.Resources;
        if (Colors.TryGetValue(name, out var pair))
        {
            resources["SystemAccentColor"] = pair.OnDark;
            foreach (var shade in LightShades)
                resources[shade] = pair.OnDark;
            foreach (var shade in DarkShades)
                resources[shade] = pair.OnLight;
        }
        else
        {
            foreach (var shade in LightShades.Concat(DarkShades).Append("SystemAccentColor"))
                resources.Remove(shade);
        }
        ThemeBrushes.AccentChanged();
        return !first;
    }
}
