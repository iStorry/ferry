using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Settings;
using Ferry.Core.Text;
using Ferry.Plugins.Torrents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.App.Services;

/// <summary>Which files of a torrent to download. A magnet's list comes from peers, which can take a while, so all of them can be chosen without waiting.</summary>
public static class TorrentFilesDialog
{
    private const double ListHeight = 280;

    /// <summary>The link to add, saying which files; null when cancelled.</summary>
    public static async Task<string?> ShowAsync(XamlRoot? root, Uri link, FerrySettings settings, RequestDetails details)
    {
        var (status, ring, list, boxes) = (new TextBlock { Text = "Asking for what is in it", Style = (Style)Application.Current.Resources["CaptionMutedStyle"], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                                           new ProgressRing { Width = 14, Height = 14, IsActive = true, VerticalAlignment = VerticalAlignment.Center },
                                           new StackPanel { Spacing = 2 },
                                           new List<(CheckBox Box, int Index)>());
        var waiting = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        waiting.Children.Add(ring);
        waiting.Children.Add(status);
        var content = new StackPanel { Spacing = 12, MinWidth = 380 };
        content.Children.Add(waiting);
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = ListHeight });

        using var reading = new CancellationTokenSource();
        using var http = new HttpFetcher(settings.ConnectTimeout);
        var read = FillAsync(TorrentFileChoice.ReadAsync(http, settings.BrowserUserAgent, link, details, reading.Token), list, boxes, waiting, status, ring, reading.Token);
        var choice = await Dialogs.ChooseAsync(root, "Which files?", content, "Add chosen", "Add all", () => true);
        await reading.CancelAsync();
        await read;
        return choice switch
        {
            ContentDialogResult.Primary when boxes.Count > 0 => TorrentFileChoice.Chosen(link, boxes.Where(row => row.Box.IsChecked == true).Select(row => row.Index), boxes.Count),
            ContentDialogResult.None => null,
            _ => link.ToString(),
        };
    }

    private static async Task FillAsync(Task<IReadOnlyList<TorrentFileEntry>> reading, StackPanel list, List<(CheckBox Box, int Index)> boxes, StackPanel waiting, TextBlock status,
                                        ProgressRing ring, CancellationToken cancel)
    {
        try
        {
            foreach (var file in await reading)
            {
                var box = new CheckBox { IsChecked = true, Content = $"{file.Path} · {Units.Size(file.Size)}" };
                list.Children.Add(box);
                boxes.Add((box, file.Index));
            }
            waiting.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // the dialog closed first: all of it, or none
        }
        catch (StageException e)
        {
            status.Text = $"{e.Message} All of it can still be added.";
            ring.IsActive = false;
        }
    }
}
