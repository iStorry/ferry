using System.Collections.ObjectModel;
using System.Globalization;
using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core.Browse;
using Ferry.Core;
using Ferry.Core.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.App.Pages;

/// <summary>
/// On the home: the albums the site lists, read by the server, or those a
/// search finds. The first page is read the first time the home shows, or once the server can be reached, and again
/// on Refresh or a search; the next page as the page scrolls near the end of the cards. Each album's own page is then
/// read for its files and size, which can hide the small ones (BrowserPage.AlbumCards.cs).
/// </summary>
public sealed partial class BrowserPage
{
    // the next page is read once the end of the cards is this near the bottom of the view
    private const double LoadMoreWithin = 600;

    private const string AlbumSizeState = "BrowserAlbumMinSize";

    // typing searches once it pauses this long
    private static readonly TimeSpan SearchAfter = TimeSpan.FromMilliseconds(350);

    private static readonly (string Label, long MinSize)[] AlbumSizes =
    [
        ("Any size", 0),
        ("100 MB and up", 100 * Units.MiB),
        ("500 MB and up", 500 * Units.MiB),
        ("1 GB and up", Units.GiB),
        ("5 GB and up", 5 * Units.GiB),
        ("10 GB and up", 10 * Units.GiB),
    ];

    private readonly ObservableCollection<AlbumCardViewModel> _shownAlbums = [];
    private List<AlbumCardViewModel> _albumCards = [];
    private CancellationTokenSource? _readingAlbums;
    private ScrollViewer? _scroller;

    /// <summary>Reads for the cards shown: details and pictures; cancelled when other cards take their place.</summary>
    private CancellationTokenSource? _fillingCards;

    /// <summary>The albums have been read, or tried, since the page loaded: they aren't read by themselves again.</summary>
    private bool _albumsTried;

    /// <summary>Reading the next page failed: no more are read by scrolling until the list is read again.</summary>
    private bool _moreFailed;

    /// <summary>Albums smaller than this are hidden once their size is known; 0 shows all.</summary>
    private long _albumMinSize;

    private DispatcherQueueTimer? _searchTimer;

    /// <summary>How far down the albums were when a page opened from them.</summary>
    private double _homeOffset;

    private void SetUpAlbums()
    {
        AlbumGrid.ItemsSource = _shownAlbums;
        var remembered = long.TryParse(_app.State(AlbumSizeState), out var minSize) ? minSize : 0;
        var index = Array.FindIndex(AlbumSizes, size => size.MinSize == remembered);
        AlbumSizeBox.ItemsSource = AlbumSizes.Select(size => size.Label).ToList();
        AlbumSizeBox.SelectedIndex = Math.Max(0, index);
        _albumMinSize = AlbumSizes[AlbumSizeBox.SelectedIndex].MinSize;
    }

    /// <summary>Follow the page's scrolling, to read more albums near the end; the list's scroll viewer is in its template.</summary>
    private void FollowAlbumScrolling()
    {
        _scroller ??= VisualTree.Descendant<ScrollViewer>(List);
        if (_scroller is { } scroller)
            scroller.ViewChanged += OnAlbumsScrolled;
    }

    private void StopFollowingAlbumScrolling()
    {
        if (_scroller is { } scroller)
            scroller.ViewChanged -= OnAlbumsScrolled;
    }

    private void ShowAlbums()
    {
        // the server gone with nothing read before: the page says so, in place of an empty list
        var down = _app.Browse.Unavailable is not null && _app.Browse.Albums is null;
        ServerDown.Visibility = down ? Visibility.Visible : Visibility.Collapsed;
        AlbumsSection.Visibility = down ? Visibility.Collapsed : Visibility.Visible;
        if (down)
        {
            ServerDownTitle.Text = $"{_app.Queue.ServerDisplay ?? "Your server"} can't be reached";
            ServerDownDetail.Text = _app.Queue.StatusDetail;
        }
        if (_readingAlbums is not null)
            return;
        if (_app.Browse.Albums is { Count: > 0 } albums && _albumCards.Count == 0)
        {
            AlbumSearch.Text = _app.Browse.AlbumsSearch;
            ShowAlbumCards(albums);
        }
        if (_app.Browse.Unavailable is not null)
            return;
        if (!_albumsTried)
            _ = ReadAlbumsAsync(more: false);
    }

    private void OnRefreshAlbums(object sender, RoutedEventArgs e) => _ = ReadAlbumsAsync(more: false, fresh: true);

    /// <summary>Try now, on what shows with the server gone: as on Downloads, setting one up if none is.</summary>
    private async void OnRetryServer(object sender, RoutedEventArgs e) => await ServerRetry.RunAsync(XamlRoot, _app);

    /// <summary>A search from the window's command box: any album open closes, and the albums for it show.</summary>
    public void SearchAlbums(string search)
    {
        CloseToHome();
        AlbumSearch.Text = search;
        _searchTimer?.Stop();
        _ = ReadAlbumsAsync(more: false);
    }

    /// <summary>Enter or the search button: search now.</summary>
    private void OnAlbumSearch(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchTimer?.Stop();
        _ = ReadAlbumsAsync(more: false);
    }

    /// <summary>
    /// Typing searches once it pauses (a single letter finds too much to be worth it), and emptying the box goes back
    /// to the latest albums. Searches aren't kept.
    /// </summary>
    private void OnAlbumSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        var text = sender.Text.Trim();
        if (_searchTimer is null)
        {
            _searchTimer = DispatcherQueue.CreateTimer();
            _searchTimer.Interval = SearchAfter;
            _searchTimer.IsRepeating = false;
            _searchTimer.Tick += (_, _) =>
            {
                if (AlbumSearch.Text.Trim() != _app.Browse.AlbumsSearch || _albumCards.Count == 0)
                    _ = ReadAlbumsAsync(more: false);
            };
        }
        _searchTimer.Stop();
        if (text.Length != 1)
            _searchTimer.Start();
    }

    /// <summary>Where the albums were scrolled to, when a page opens from them (they are what shows until it does).</summary>
    private void RememberHomePlace()
    {
        if (Home.Visibility == Visibility.Visible && _scroller is { } scroller)
            _homeOffset = scroller.VerticalOffset;
    }

    /// <summary>
    /// Back to that place once the albums are laid out again: they come back over a few layout passes, and scrolling
    /// before they are tall enough would stop short.
    /// </summary>
    private void ReturnToHomePlace()
    {
        const int MaxPasses = 10;
        var passes = 0;
        void Restore(object? sender, object e)
        {
            if (_scroller is not { } scroller || Home.Visibility != Visibility.Visible)
            {
                List.LayoutUpdated -= Restore;
                return;
            }
            if (scroller.ExtentHeight - scroller.ViewportHeight < _homeOffset && ++passes < MaxPasses)
                return;
            List.LayoutUpdated -= Restore;
            scroller.ChangeView(null, _homeOffset, null, disableAnimation: true);
        }
        List.LayoutUpdated += Restore;
    }

    private void OnAlbumsScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        LoadMoreIfNear();
        PinTools();
    }

    /// <summary>Read the next page when the end of the cards is near the bottom of the view, or already above it.</summary>
    private void LoadMoreIfNear()
    {
        if (_scroller is not { } scroller || Home.Visibility != Visibility.Visible || _readingAlbums is not null || _moreFailed
            || _albumCards.Count == 0 || _app.Browse.AlbumsEnded)
            return;
        // while sizes are still being read, hidden cards would make the end look near: wait for them
        if (_albumMinSize > 0 && _albumCards.Any(card => !card.DetailsRead))
            return;
        if (scroller.VerticalOffset + scroller.ViewportHeight >= scroller.ExtentHeight - LoadMoreWithin)
            _ = ReadAlbumsAsync(more: true);
    }

    /// <summary>The first page for the search in the box, or with <paramref name="more"/> the next page of the albums shown.</summary>
    /// <param name="fresh">read it again, even when it was read lately (Refresh)</param>
    private async Task ReadAlbumsAsync(bool more, bool fresh = false)
    {
        if (more && _readingAlbums is not null)
            return;
        _readingAlbums?.Cancel();
        using var reading = new CancellationTokenSource();
        _readingAlbums = reading;
        _albumsTried = true;
        RefreshAlbumsButton.IsEnabled = false;
        var search = AlbumSearch.Text.Trim();
        if (more)
        {
            AlbumsMoreRing.IsActive = true;
            AlbumsMore.Visibility = Visibility.Visible;
        }
        else
        {
            _moreFailed = false;
            var where = ServerMode ? " on the server" : "";
            ShowAlbumsStatus(search.Length > 0 ? $"Searching{where} for “{search}”…" : $"Reading albums{where}…", busy: true);
        }
        var read = false;
        try
        {
            var albums = await _app.Browse.ReadAlbumsAsync(search, more, reading.Token, fresh);
            if (more)
                AddAlbumCards(albums);
            else
                ShowAlbumCards(albums);
            read = true;
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested)
        {
            // the page closed, or a newer read took over
        }
        catch (StageException ex)
        {
            _moreFailed = more;
            ShowAlbumsStatus(more ? $"Couldn't read more albums: {ex.Message} Refresh to try again." : $"Couldn't read the albums: {ex.Message}",
                             busy: false);
        }
        finally
        {
            if (ReferenceEquals(_readingAlbums, reading))
            {
                _readingAlbums = null;
                RefreshAlbumsButton.IsEnabled = true;
                AlbumsMoreRing.IsActive = false;
                AlbumsMore.Visibility = Visibility.Collapsed;
                if (read)
                {
                    ShowAlbumsNote();
                    // a page of cards may not fill the view: once they are laid out, read on if the end is still near
                    DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, LoadMoreIfNear);
                }
            }
        }
    }

    /// <summary>These albums in place of those shown.</summary>
    private void ShowAlbumCards(IReadOnlyList<AlbumCard> albums)
    {
        _fillingCards?.Cancel();
        _fillingCards?.Dispose();
        var filling = new CancellationTokenSource();
        _fillingCards = filling;
        _albumCards = [.. albums.Select(album => new AlbumCardViewModel(album))];
        _shownAlbums.Clear();
        foreach (var card in _albumCards.Where(Shows))
            _shownAlbums.Add(card);
        ShowAlbumsNote();
        _ = FillCardsAsync(_albumCards, filling.Token);
    }

    /// <summary>The next page's albums after those shown.</summary>
    private void AddAlbumCards(IReadOnlyList<AlbumCard> albums)
    {
        if (_fillingCards is not { } filling)
        {
            ShowAlbumCards(albums);
            return;
        }
        var cards = albums.Select(album => new AlbumCardViewModel(album)).ToList();
        _albumCards.AddRange(cards);
        foreach (var card in cards.Where(Shows))
            _shownAlbums.Add(card);
        _ = FillCardsAsync(cards, filling.Token);
    }

    private void OnAlbumSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AlbumSizeBox.SelectedIndex < 0 || AlbumSizes[AlbumSizeBox.SelectedIndex].MinSize == _albumMinSize)
            return;
        _albumMinSize = AlbumSizes[AlbumSizeBox.SelectedIndex].MinSize;
        _app.SetState(AlbumSizeState, _albumMinSize == 0 ? null : _albumMinSize.ToString(CultureInfo.InvariantCulture));
        foreach (var card in _albumCards)
            Place(card);
        ShowAlbumsNote();
        // pictures of cards shown only now
        if (_fillingCards is { IsCancellationRequested: false } filling)
            _ = LoadPicturesAsync([.. _shownAlbums.Where(card => !card.PictureAsked)], filling.Token);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, LoadMoreIfNear);
    }

    private async Task LoadPicturesAsync(IReadOnlyList<AlbumCardViewModel> cards, CancellationToken cancel)
    {
        try
        {
            await Task.WhenAll(cards.Select(card => InSlotAsync(_pictureSlots, () => LoadPictureAsync(card, cancel), cancel)));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // other cards took their place, or the page closed
        }
    }

    /// <summary>
    /// Shown with no size to hide by, else once its size is known and big enough; one whose page couldn't be read, or
    /// doesn't say its size, is shown.
    /// </summary>
    private bool Shows(AlbumCardViewModel card) =>
        _albumMinSize == 0 || (card.DetailsRead && (card.Size is null || card.Size >= _albumMinSize));

    /// <summary>Show or hide the card as <see cref="Shows"/> says, in the list's order.</summary>
    private void Place(AlbumCardViewModel card)
    {
        var shown = _shownAlbums.Contains(card);
        if (shown == Shows(card))
            return;
        if (shown)
        {
            _shownAlbums.Remove(card);
            return;
        }
        var index = _albumCards.TakeWhile(other => !ReferenceEquals(other, card)).Count(_shownAlbums.Contains);
        _shownAlbums.Insert(index, card);
    }

    /// <summary>Under the header, quietly: sizes still being read, how many albums are hidden, or that none are found.</summary>
    private void ShowAlbumsNote()
    {
        if (_readingAlbums is not null)
            return;
        var unread = _albumMinSize > 0 ? _albumCards.Count(card => !card.DetailsRead) : 0;
        var hidden = _albumCards.Count(card => card.DetailsRead && !Shows(card));
        var under = AlbumSizes.First(size => size.MinSize == _albumMinSize).Label.Replace(" and up", "", StringComparison.Ordinal);
        var search = _app.Browse.AlbumsSearch;
        AlbumsCaption.Text = search.Length > 0 ? $"Found for “{search}”" : "Latest";
        if (_albumCards.Count == 0)
            ShowAlbumsStatus(search.Length > 0 ? $"No albums match “{search}”." : "No albums are listed right now.", busy: false);
        else if (unread > 0)
            ShowAlbumsStatus($"Reading album sizes… {_albumCards.Count - unread} of {_albumCards.Count}", busy: true);
        else if (hidden > 0)
            ShowAlbumsStatus(hidden == 1 ? $"1 album under {under} hidden" : $"{hidden} albums under {under} hidden", busy: false);
        else
        {
            AlbumsRing.IsActive = false;
            AlbumsStatus.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowAlbumsStatus(string text, bool busy)
    {
        AlbumsText.Text = text;
        AlbumsRing.IsActive = busy;
        AlbumsStatus.Visibility = Visibility.Visible;
    }
}
