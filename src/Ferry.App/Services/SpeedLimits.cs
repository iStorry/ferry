using Ferry.Core.Settings;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;

namespace Ferry.App.Services;

/// <summary>The overall speed limit, as Downloads and the command box both change it.</summary>
public static class SpeedLimits
{
    /// <summary>"10 MB/s", or "No limit".</summary>
    public static string Label(FerrySettings settings)
    {
        var limit = settings.SpeedLimit;
        return limit > 0 ? Units.Speed(limit) : "No limit";
    }

    /// <summary>Whether the environment has set it, so it can't be changed here.</summary>
    public static bool IsLocked(FerrySettings settings) => settings[SettingKey.SpeedLimit].Source == SettingSource.Environment;

    /// <summary>Set it; what went wrong is said.</summary>
    /// <param name="value">as the setting takes it, such as 10M, or 0 for none</param>
    public static async Task SetAsync(XamlRoot? root, AppServices app, string value)
    {
        try
        {
            app.Change(store => store.Set(SettingKey.SpeedLimit.Name, value));
        }
        catch (SettingsException e)
        {
            await Dialogs.ShowErrorAsync(root, "The speed limit wasn't changed", e.Message);
        }
    }

    /// <summary>Ask for a limit, in MB/s or as a size.</summary>
    public static async Task AskAsync(XamlRoot? root, AppServices app)
    {
        var answer = await Dialogs.AskAsync(root, "Speed limit", "MB/s for all downloads together, or a size such as 500K; 0 for none", "", "Set");
        if (answer is null)
            return;
        if (TryParse(answer, out _))
            await SetAsync(root, app, AsSize(answer));
        else
            await SayNotASpeedAsync(root, answer);
    }

    /// <summary>An answer to how fast: a bare number is in MB/s, whatever the culture; otherwise a size such as 500K.</summary>
    public static bool TryParse(string answer, out long bytes) => SettingKey.TryParseSize(AsSize(answer), out bytes);

    public static Task SayNotASpeedAsync(XamlRoot? root, string answer) =>
        Dialogs.ShowErrorAsync(root, "Not a speed", $"\"{answer}\" is not a speed such as 5 (MB/s) or 500K.");

    /// <summary>The answer as the setting takes it.</summary>
    private static string AsSize(string answer)
    {
        var text = answer.Trim();
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var megabytes)
            ? FormattableString.Invariant($"{megabytes}M")
            : text;
    }
}
