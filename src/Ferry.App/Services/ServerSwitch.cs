using Ferry.Core.Settings;
using Ferry.Plugins.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.App.Services;

/// <summary>Turning Server Support on or off, from whichever page asks.</summary>
public static class ServerSwitch
{
    /// <summary>
    /// From a switch. Turning it on asks first, and asks where downloads are to run from now on: on the server, or on this PC
    /// as before, with the server there for when it is wanted. That can be changed any time, on its page.
    /// </summary>
    /// <returns>whether it is now as asked</returns>
    public static async Task<bool> ToggleAsync(XamlRoot? root, AppServices app, bool on)
    {
        if (!on)
            return await SetAsync(root, app, on);
        var where = await Dialogs.ChooseAsync(root, "Add Server Support?",
            "Downloads can run on your own Linux server, even while this PC is off, and come here over SSH when they are done. Server joins the "
            + "sidebar, and Ferry asks for the server next. Nothing is downloaded to add it.\n\nWhere should downloads run from now on? You can "
            + "change this any time on Server Support's page.",
            "On my server", "On this PC", () => true);
        if (where == ContentDialogResult.None || !await SetAsync(root, app, on))
            return false;
        // set by the environment, it stays as it is there, and the page says so
        if (app.Settings[ServerKeys.Mode].Source != SettingSource.Environment
            && await SetModeAsync(root, app, server: where == ContentDialogResult.Primary) is { } problem)
            await Dialogs.ShowErrorAsync(root, "Where downloads run wasn't changed", problem);
        return true;
    }

    /// <summary>Turn it on or off; off while files are on the server says first what happens to them, and nothing is deleted.</summary>
    /// <returns>whether it is now as asked</returns>
    public static async Task<bool> SetAsync(XamlRoot? root, AppServices app, bool on)
    {
        if (!on && !await ConfirmOffAsync(root, app))
            return false;
        return await PluginSwitch.SaveAsync(root, app, ServerKeys.Enabled, "Server Support", on);
    }

    /// <summary>Download on the server or on this PC; leaving the server with files still on it says first what happens to them.</summary>
    /// <returns>why that couldn't be saved; null when it was, or the user chose to stay</returns>
    public static async Task<string?> SetModeAsync(XamlRoot? root, AppServices app, bool server)
    {
        if (!server && !await ConfirmLeavingAsync(root, app))
            return null;
        try
        {
            app.Change(store => store.Set(ServerKeys.Mode.Name, server ? "server" : "local"));
            return null;
        }
        catch (SettingsException e)
        {
            return e.Message;
        }
    }

    private static async Task<bool> ConfirmOffAsync(XamlRoot? root, AppServices app)
    {
        if (app.Settings.Mode != DownloadMode.Server)
            return true;
        return await Dialogs.ConfirmAsync(root, "Turn off Server Support?",
                                          $"{GoingBack(app, UnfinishedFiles(app))}\n\nYour login stays, so turning it back on picks up where it left off. To remove the login, use Forget this server.",
                                          "Turn off", "Keep it on", primaryIsDefault: false, tone: DialogTone.Caution);
    }

    private static async Task<bool> ConfirmLeavingAsync(XamlRoot? root, AppServices app)
    {
        var files = UnfinishedFiles(app);
        if (app.Settings.Mode != DownloadMode.Server || files == 0)
            return true;
        return await Dialogs.ConfirmAsync(root, "Download on this PC?",
                                          $"{GoingBack(app, files)}\n\nChoosing the server again picks up where it left off.",
                                          "Use this PC", "Keep the server", primaryIsDefault: false, tone: DialogTone.Caution);
    }

    private static int UnfinishedFiles(AppServices app) => PluginSwitch.UnfinishedFiles(app, _ => true);

    private static string GoingBack(AppServices app, int files)
    {
        var server = app.Queue.ServerDisplay ?? "your server";
        return files > 0
            ? $"Ferry goes back to downloading on this PC. {files} file{(files == 1 ? " is" : "s are")} still on {server}, and the agent keeps working on {(files == 1 ? "it" : "them")}."
            : "Ferry goes back to downloading on this PC.";
    }
}
