using Ferry.App.ViewModels;
using Ferry.Core.Browse;
using Ferry.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.App.Pages;

/// <summary>With nothing open: the watched albums. With an album open: watching it.</summary>
public sealed partial class BrowserPage
{
    private List<WatchedAlbumViewModel> _watched = [];

    private void ShowHome()
    {
        var empty = _page is null && Reading.Visibility == Visibility.Collapsed;
        Home.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty)
            return;
        _watched = [.. _app.Browse.Watcher.Albums.Select(album => new WatchedAlbumViewModel(album))];
        WatchedList.ItemsSource = _watched;
        WatchedSection.Visibility = _watched.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowAlbums();
    }

    /// <summary>The reader or watcher changed with the mode or the connection, or the watched albums did.</summary>
    private void OnBrowseChanged()
    {
        ShowWhere();
        ShowHome();
        ShowWatch();
        ShowFacts();
    }

    /// <summary>Where albums come from and are read: this PC, or the server.</summary>
    private void ShowWhere() => WhereText.Text = $"Albums from {_app.Browse.SiteName} · {_app.Browse.Where}";

    private void ShowWatch()
    {
        if (_page is not { } page)
            return;
        var album = page.Link.Kind == PageKind.Album;
        WatchButton.Visibility = album ? Visibility.Visible : Visibility.Collapsed;
        var watching = album && _app.Browse.Watcher.Find(page.Link) is not null;
        WatchButton.IsChecked = watching;
        WatchText.Text = watching ? "Watching" : "Watch";
    }

    private async void OnWatchToggled(object sender, RoutedEventArgs e)
    {
        if (_page is not { Link.Kind: PageKind.Album } page)
            return;
        var watch = WatchButton.IsChecked == true;
        await WithWatcherAsync(watcher => watch ? watcher.WatchAsync(page) : watcher.UnwatchAsync(page.Link));
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        try
        {
            await WithWatcherAsync(watcher => watcher.CheckNowAsync(CancellationToken.None));
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    /// <summary>A watched album's More button: open it, or stop watching it.</summary>
    private void OnWatchedMore(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || WatchedOf(sender) is not { } row)
            return;
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Open", OpenGlyph, () => Open(row.Album.Link)));
        menu.Items.Add(MenuItem("Stop watching", "\uE711", () => _ = WithWatcherAsync(watcher => watcher.UnwatchAsync(row.Album.ToPageLink()))));
        menu.ShowAt(button);
    }

    /// <summary>The watched album a control on its card belongs to.</summary>
    private WatchedAlbumViewModel? WatchedOf(object sender) => ItemOf(sender, WatchedList, _watched);

    /// <summary>
    /// Change the watched albums. Server mode's go through the agent: when it can't be reached, or refuses, the page
    /// says so and shows the albums as they still are.
    /// </summary>
    private async Task WithWatcherAsync(Func<IAlbumWatcher, Task> change)
    {
        try
        {
            await change(_app.Browse.Watcher);
        }
        catch (Exception e) when (e is StageException or InvalidOperationException)
        {
            ShowProblem("Couldn't change the watched albums", e.Message);
            ShowWatch();
        }
    }
}
