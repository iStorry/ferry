using System.Diagnostics;
using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core;
using Ferry.Core.Queue;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Ferry.App.Pages;

/// <summary>What can be done with a file, a section or the queue, and what goes wrong doing it is said.</summary>
public sealed partial class DownloadsPage
{
    private async void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var selected = List.SelectedItems.OfType<ItemViewModel>().ToList();
        if (selected.Count == 0 || e.OriginalSource is not ListViewItem)
            return; // typing in the link box, or a button in the header
        if (e.Key == VirtualKey.Delete)
        {
            e.Handled = true;
            await RemoveAsync(selected);
        }
        else if (e.Key == VirtualKey.Space)
        {
            e.Handled = true;
            await RunAllAsync(selected, (q, row) => row.CanResume ? q.ResumeAsync(row.Id) : q.PauseAsync(row.Id));
        }
    }

    private static ItemViewModel Row(object sender) => (ItemViewModel)((FrameworkElement)sender).DataContext;

    private async void OnPause(object sender, RoutedEventArgs e) => await RunAsync(q => q.PauseAsync(Row(sender).Id));

    private async void OnResume(object sender, RoutedEventArgs e) => await RunAsync(q => q.ResumeAsync(Row(sender).Id));

    private async void OnCancel(object sender, RoutedEventArgs e) => await RunAsync(q => q.CancelAsync(Row(sender).Id));

    private async void OnRetry(object sender, RoutedEventArgs e) => await RunAsync(q => q.RetryAsync(Row(sender).Id));

    private void OnOpenFolder(object sender, RoutedEventArgs e) => OpenFolder(HerePath(Row(sender)));

    /// <summary>The file's path on this PC; null while it isn't here (still downloading, on the server, or on a remote queue).</summary>
    private static string? HerePath(ItemViewModel row) => row.CanOpenFolder ? row.Item.Path : null;

    private void OnOpenDownloadFolder(object sender, RoutedEventArgs e) => OpenFolder(null);

    private static AlbumGroupViewModel Album(object sender) => (AlbumGroupViewModel)((FrameworkElement)sender).DataContext;

    /// <summary>Pause the album's files, or if they are stopped, carry on with them.</summary>
    private async void OnPauseAlbum(object sender, RoutedEventArgs e)
    {
        var album = Album(sender);
        await RunAllAsync(album.Rows.ToList(), (q, row) => !album.IsStopped ? q.PauseAsync(row.Id)
            : row.Item.State == QueueState.Paused ? q.ResumeAsync(row.Id)
            : q.RetryAsync(row.Id));
    }

    private void OnOpenAlbumFolder(object sender, RoutedEventArgs e) => OpenFolder(Album(sender).Rows is { Count: > 0 } rows ? rows[0].Item.Folder : null);

    private void OnToggleDoneFiles(object sender, RoutedEventArgs e)
    {
        _groups[(int)QueueSection.Done].ToggleFiles();
        ShowSummaries();
    }

    /// <summary>Done files leave the list; the files themselves stay where they are.</summary>
    private async void OnClearDone(object sender, RoutedEventArgs e)
    {
        await RunAllAsync(_groups[(int)QueueSection.Done].Rows.ToList(), (q, row) => q.RemoveAsync(row.Id, deleteParts: false));
    }

    private void OnOpenFolderAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenFolder(null);
    }

    private async void OnPauseAll(object sender, RoutedEventArgs e) => await RunAsync(q => q.PauseAllAsync());

    private async void OnResumeAll(object sender, RoutedEventArgs e) => await RunAsync(q => q.ResumeAllAsync());

    private void OnMore(object sender, RoutedEventArgs e)
    {
        var row = Row(sender);
        var menu = new MenuFlyout();
        void Add(string text, string glyph, bool enabled, Func<Task> action, string? key = null)
        {
            var entry = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            if (key is not null)
                entry.KeyboardAcceleratorTextOverride = key;
            entry.Click += async (_, _) => await action();
            menu.Items.Add(entry);
        }
        Add(row.CanResume ? "Resume" : "Pause", row.CanResume ? "\uE768" : "\uE769", row.CanPause || row.CanResume,
            () => RunAsync(q => row.CanResume ? q.ResumeAsync(row.Id) : q.PauseAsync(row.Id)), "Space");
        Add("Cancel", "\uE71A", row.CanCancel, () => RunAsync(q => q.CancelAsync(row.Id)));
        Add("Retry", "\uE72C", row.CanRetry, () => RunAsync(q => q.RetryAsync(row.Id)));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Rename…", "\uE70F", row.CanRename, () => RenameAsync(row));
        // the limit is the download's: there is none once the file is downloaded (on the server, coming here, done)
        Add("Speed limit for this file…", "\uEC4A",
            row.Item.State is not (QueueState.Done or QueueState.Skipped or QueueState.OnServer or QueueState.Transferring), () => LimitAsync(row));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Open folder", "\uE838", _context.Where != DownloadPlace.RemoteQueue, () => { OpenFolder(HerePath(row)); return Task.CompletedTask; });
        Add("Copy link", "\uE71B", true, () => { CopyLink(row); return Task.CompletedTask; });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Remove", "\uE74D", true, () => RemoveAsync([row]), "Del");
        menu.ShowAt((FrameworkElement)sender);
    }

    private async Task RunAsync(Func<IDownloadQueue, Task> action)
    {
        if (Queue is not { } queue)
            return;
        try
        {
            await action(queue);
        }
        catch (Exception ex) when (ex is StageException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, "That didn't work", ex.Message);
        }
    }

    private async Task RenameAsync(ItemViewModel row)
    {
        var name = await Dialogs.AskAsync(XamlRoot, "Rename", "Save the file as", row.Item.Name ?? row.Name, "Rename");
        if (name is not null)
            await RunAsync(q => q.RenameAsync(row.Id, name));
    }

    private async Task LimitAsync(ItemViewModel row)
    {
        var current = row.Item.SpeedLimit > 0 ? (row.Item.SpeedLimit / (double)Units.MiB).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "0";
        var answer = await Dialogs.AskAsync(XamlRoot, "Speed limit for this file", "MB/s, or a size such as 500K; 0 for none", current, "Set");
        if (answer is null)
            return;
        if (SpeedLimits.TryParse(answer, out var bytes))
            await RunAsync(q => q.SetSpeedLimitAsync(row.Id, bytes));
        else
            await SpeedLimits.SayNotASpeedAsync(XamlRoot, answer);
    }

    private async Task RemoveAsync(List<ItemViewModel> rows)
    {
        if (Queue is null)
            return;
        var unfinished = rows.Where(r => r.Item.State is not (QueueState.Done or QueueState.Skipped)).ToList();
        var deleteParts = false;
        if (unfinished.Count > 0)
        {
            var check = new CheckBox { Content = "Also delete what was downloaded", IsChecked = true };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = unfinished.Count == 1
                    ? $"{unfinished[0].Name} isn't finished{(unfinished[0].Item.Done > 0 ? $" ({Units.Size(unfinished[0].Item.Done)} so far)" : "")}."
                    : $"{unfinished.Count} files aren't finished.",
            });
            content.Children.Add(check);
            content.Children.Add(new TextBlock
            {
                Text = "Otherwise adding the link again continues where it stopped.",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionMutedStyle"],
            });
            var title = rows.Count == 1 ? $"Remove {rows[0].Name}?" : $"Remove {rows.Count} files?";
            if (!await Dialogs.ConfirmAsync(XamlRoot, title, content, "Remove", "Cancel", tone: DialogTone.Caution))
                return;
            deleteParts = check.IsChecked == true;
        }
        await RunAllAsync(rows, (q, row) => q.RemoveAsync(row.Id, deleteParts));
    }

    /// <summary><paramref name="action"/> on each row; what failed is said once, in one dialog.</summary>
    private async Task RunAllAsync(IReadOnlyCollection<ItemViewModel> rows, Func<IDownloadQueue, ItemViewModel, Task> action)
    {
        const int MostToName = 5;
        if (Queue is not { } queue)
            return;
        var failed = new List<string>();
        foreach (var row in rows)
        {
            try
            {
                await action(queue, row);
            }
            catch (Exception ex) when (ex is StageException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                failed.Add($"{row.Name}: {ex.Message}");
            }
        }
        if (failed.Count > 0)
            await Dialogs.ShowErrorAsync(XamlRoot, failed.Count == 1 ? "That didn't work" : $"That didn't work for {failed.Count} files",
                                         string.Join("\n", failed.Take(MostToName)) + (failed.Count > MostToName ? $"\n… and {failed.Count - MostToName} more" : ""));
    }

    private void CopyLink(ItemViewModel row)
    {
        var link = row.Item.Link.Original.ToString();
        if (ClipboardText.TryCopy(link))
            Show(InfoBarSeverity.Success, "Link copied", link);
        else
            Show(InfoBarSeverity.Warning, "Couldn't copy the link", ClipboardText.Held);
    }

    /// <summary>
    /// Show <paramref name="path"/> in Explorer, or the download folder: also for a path that means nothing here, as the
    /// server's own paths do ("~/.ferry/downloads/Album"), which would make a folder of that name wherever Ferry runs from.
    /// </summary>
    private void OpenFolder(string? path)
    {
        var here = path is not null && Path.IsPathFullyQualified(path) ? path : null;
        try
        {
            var start = here is not null && File.Exists(here)
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{here}\"")
                : new ProcessStartInfo("explorer.exe", $"\"{Directory.CreateDirectory(FolderToOpen(here)).FullName}\"");
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Show(InfoBarSeverity.Warning, "Couldn't open the folder", ex.Message); // a drive that's gone, say
        }
    }

    private string FolderToOpen(string? here) =>
        here is null ? _context.DownloadFolder
        : Directory.Exists(here) ? here
        : Path.GetDirectoryName(here) ?? _context.DownloadFolder;
}
