using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Microsoft.UI.Xaml;

namespace Ferry.App.Services;

/// <summary>Turning a plugin on or off: what every plugin shares, and all there is to it for one that only adds sites.</summary>
public static class PluginSwitch
{
    /// <summary>From a switch: a plugin is an option, so what turning it on adds is asked about first.</summary>
    /// <param name="adds">what it adds, for the question</param>
    /// <returns>whether it is now as asked</returns>
    public static async Task<bool> ToggleAsync(XamlRoot? root, AppServices app, SitePlugin plugin, string adds, bool on) =>
        (!on || await Dialogs.ConfirmAsync(root, $"Add {plugin.Name}?", $"{adds} {(app.Installed.Find(plugin) is null ? "It comes with Ferry, so nothing is downloaded." : "It runs on your server.")}", "Add", "Not now"))
        && await SetAsync(root, app, plugin, on);

    /// <summary>Turn it on or off; off with its files in the queue says first what becomes of them, and nothing is deleted.</summary>
    /// <returns>whether it is now as asked</returns>
    public static async Task<bool> SetAsync(XamlRoot? root, AppServices app, SitePlugin plugin, bool on)
    {
        if (!on && !await ConfirmOffAsync(root, app, plugin))
            return false;
        return await SaveAsync(root, app, plugin.Enabled, plugin.Name, on);
    }

    /// <returns>whether it was saved; if not, the user has been told why</returns>
    public static async Task<bool> SaveAsync(XamlRoot? root, AppServices app, SettingKey enabled, string name, bool on)
    {
        try
        {
            app.Change(store => store.Set(enabled.Name, on ? "true" : "false"));
            return true;
        }
        catch (SettingsException e)
        {
            await Dialogs.ShowErrorAsync(root, $"{name} wasn't turned {(on ? "on" : "off")}", e.Message);
            return false;
        }
    }

    /// <summary>How many files in the queue, not finished yet, came from the sites <paramref name="isOwn"/> picks out.</summary>
    public static int UnfinishedFiles(AppServices app, Func<string, bool> isOwn) =>
        (app.Queue.Queue?.Items ?? []).Count(item => isOwn(item.Link.SiteId) && item.State is not (QueueState.Done or QueueState.Skipped or QueueState.Cancelled));

    private static async Task<bool> ConfirmOffAsync(XamlRoot? root, AppServices app, SitePlugin plugin)
    {
        var files = UnfinishedFiles(app, plugin.Owns);
        if (files == 0)
            return true;
        var what = files == 1
            ? $"1 file in Downloads came through {plugin.Name}. It stays there, paused, to resume once it is back on. Nothing is deleted."
            : $"{files} files in Downloads came through {plugin.Name}. They stay there, paused, to resume once it is back on. Nothing is deleted.";
        return await Dialogs.ConfirmAsync(root, $"Turn off {plugin.Name}?", what, "Turn off", "Keep it on", primaryIsDefault: false, tone: DialogTone.Caution);
    }
}
