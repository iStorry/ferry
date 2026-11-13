using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Plugins;
using Ferry.Plugins.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Ferry.App.Services;

/// <summary>
/// Installing a plugin, from a file or a link, and removing one. A package is read and checked before anything is asked,
/// and nothing is kept until the user has seen whose it is and what it can do.
/// </summary>
public static class PluginInstall
{
    private const string NotInstalled = "The plugin wasn't installed";

    /// <returns>the plugin's name when one was installed</returns>
    public static async Task<string?> FromFileAsync(XamlRoot? root, AppServices app)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(PluginPackage.Extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window!.Handle);
        return await picker.PickSingleFileAsync() is { } file ? await InstallAsync(root, app, file.Path) : null;
    }

    /// <returns>the plugin's name when one was installed</returns>
    public static async Task<string?> FromLinkAsync(XamlRoot? root, AppServices app)
    {
        if (await Dialogs.AskAsync(root, "Add a plugin from a link", "The link to its .ferryplugin file", "", "Next") is not { } text)
            return null;
        if (!PluginDownload.TryReadLink(text, out var link, out var reason))
        {
            await Dialogs.ShowErrorAsync(root, NotInstalled, reason);
            return null;
        }
        string? file = null;
        try
        {
            using var http = new HttpFetcher(app.Settings.ConnectTimeout);
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var download = new PluginDownload(http);
            var folder = Path.Combine(Path.GetTempPath(), "Ferry-plugins");
            try
            {
                file = await download.FetchAsync(link, folder, limit.Token);
            }
            catch (StageException e) when (e.Status == System.Net.HttpStatusCode.NotFound && GitHubReleaseFile.TryRead(link) is { } release)
            {
                // GitHub says "not found" of a repository only some may read: with a token of theirs it may be there after all
                if (await AskTokenAsync(root, release) is not { } token)
                    return null;
                file = await download.FetchAsync(release, token, folder, limit.Token);
            }
            return await InstallAsync(root, app, file, link.Host);
        }
        catch (Exception e) when (e is StageException or PluginException or OperationCanceledException)
        {
            await Dialogs.ShowErrorAsync(root, NotInstalled, e is OperationCanceledException ? $"{link.Host} took too long to send it." : e.Message);
            return null;
        }
        finally
        {
            if (file is not null)
                File.Delete(file);
        }
    }

    /// <summary>A GitHub access token for a release that isn't public; null when none was given. It is used for this download and not kept.</summary>
    private static async Task<string?> AskTokenAsync(XamlRoot? root, GitHubReleaseFile release)
    {
        var box = new PasswordBox { Header = "Access token", PlaceholderText = "github_pat_…" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"GitHub has no public file there. If {release.Owner}/{release.Repo} is a repository only some people may read, give a token that can read "
                   + "its contents: a fine-grained one for that repository alone, with Contents set to read-only. Ferry sends it to GitHub for this "
                   + "download and doesn't keep it.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        return await Dialogs.ConfirmAsync(root, "This release isn't public", panel, "Download", "Cancel") && box.Password.Trim() is { Length: > 0 } token
            ? token
            : null;
    }

    /// <summary>Read the package, ask, keep it, and have the server get it.</summary>
    /// <param name="from">where it came from, when not from this PC</param>
    private static async Task<string?> InstallAsync(XamlRoot? root, AppServices app, string file, string? from = null)
    {
        try
        {
            var package = await Task.Run(() => PluginPackage.Read(file));
            var manifest = package.Manifest;
            var before = app.Installed.Find(manifest.Id);
            if (before?.Stored.Package.Sha256 == package.Sha256)
            {
                await Dialogs.ShowErrorAsync(root, $"{manifest.Name} is installed already", $"This is the same build of it, {manifest.Version}.");
                return null;
            }
            var title = before is null ? $"Install {manifest.Name}?" : $"Replace {manifest.Name} {before.Stored.Manifest.Version} with {manifest.Version}?";
            if (!await Dialogs.ConfirmAsync(root, title, Facts(package, before, from), before is null ? "Install" : "Replace", "Cancel", primaryIsDefault: false))
                return null;
            await Task.Run(() => app.Installed.Install(package, file));
            await TakeEffectAsync(app);
            return manifest.Name;
        }
        catch (Exception e) when (e is PluginException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(root, NotInstalled, e.Message);
            return null;
        }
    }

    /// <returns>whether it was removed</returns>
    public static async Task<bool> RemoveAsync(XamlRoot? root, AppServices app, InstalledPlugin installed)
    {
        var name = installed.Stored.Manifest.Name;
        var files = PluginSwitch.UnfinishedFiles(app, installed.Plugin.Owns);
        var words = "It is taken off this PC, and off your server the next time Ferry connects. Its settings are kept, in case you install it again."
                    + (files > 0 ? $" {files} of its files in the queue will stop." : "");
        if (!await Dialogs.ConfirmAsync(root, $"Remove {name}?", words, "Remove", "Cancel", primaryIsDefault: false))
            return false;
        try
        {
            app.Installed.Remove(installed);
            await TakeEffectAsync(app);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(root, $"{name} wasn't removed", e.Message);
            return false;
        }
    }

    /// <summary>The settings are read again, now with the plugin's or without, and a server in use is given what changed.</summary>
    private static async Task TakeEffectAsync(AppServices app)
    {
        app.Reload();
        if (app.Settings.Mode == DownloadMode.Server)
            await app.Queue.SwitchAsync();
    }

    /// <summary>What is known of a package before it is installed: whose it says it is, whether that is signed for, and what it will be able to do.</summary>
    private static StackPanel Facts(PluginPackage package, InstalledPlugin? before, string? from)
    {
        var manifest = package.Manifest;
        var panel = new StackPanel { Spacing = 10 };
        void Say(string text, bool quiet = false) => panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Style = quiet ? (Style)Application.Current.Resources["CaptionMutedStyle"] : null,
        });
        Say($"{manifest.What}");
        Say($"Version {manifest.Version}, by {manifest.Publisher}{(from is null ? "" : $", from {from}")}.");
        var signer = package.SignedBy;
        Say(signer is null
                ? "It isn't signed, so nothing shows who made it or that it is as they left it."
                : before?.Stored.Package.SignedBy is { } known && known != signer
                    ? $"It is signed by another key than the {manifest.Name} you have. Key {Short(signer)}; yours is {Short(known)}."
                    : $"It is signed, so it is as the holder of key {Short(signer)} left it. That doesn't say who they are.");
        Say("A plugin is a program. It runs on your server, as the user Ferry signs in as, and can do whatever that user can: read, change and send "
            + "anything there. Ferry doesn't hold it back. Install it only if you trust who gave it to you.");
        Say($"SHA-256 {package.Sha256}", quiet: true);
        return panel;
    }

    private static string Short(string fingerprint) => fingerprint[..16];
}
