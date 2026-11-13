using System.Runtime.InteropServices;
using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core.Browse;
using Ferry.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ferry.App.Pages;

/// <summary>An album's card: laid out in the grid, filled in with what its own page says and its picture once it is shown, and what can be done with it.</summary>
public sealed partial class BrowserPage
{
    // reads at the same time: enough to fill the first row soon, few enough not to crowd the site
    private const int PicturesAtOnce = 4;
    private const int DetailsAtOnce = 3;

    // as many covers a row as fit at this width or wider
    private const double MinCardWidth = 164;
    private const double CardSpacing = 14;

    // a card is this much taller than wide, so the picture is square: the gap (9), two lines of name (36) and a line of
    // facts (16) under it
    private const double CardTextHeight = 61;

    // what opens a page, on its menus
    private const string OpenGlyph = "\uE8A7";

    // a pointer resting this long on a card reads its album ahead
    private static readonly TimeSpan ReadAheadAfter = TimeSpan.FromMilliseconds(400);

    private readonly Slots _detailSlots = new(DetailsAtOnce);
    private readonly Slots _pictureSlots = new(PicturesAtOnce);
    private DispatcherQueueTimer? _aheadTimer;
    private AlbumCardViewModel? _aheadCard;

    // a resting pointer reads the card's album ahead, so opening it is quick
    private void OnAlbumCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _aheadCard = CardOf(sender);
        _aheadCard?.SetPointerOver(true);
        if (_aheadTimer is null)
        {
            _aheadTimer = DispatcherQueue.CreateTimer();
            _aheadTimer.Interval = ReadAheadAfter;
            _aheadTimer.IsRepeating = false;
            _aheadTimer.Tick += (_, _) =>
            {
                if (_aheadCard is { } card)
                    _app.Browse.ReadAhead(card.Album.Link);
            };
        }
        _aheadTimer.Stop();
        if (_aheadCard is not null)
            _aheadTimer.Start();
    }

    private void OnAlbumCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _aheadCard?.SetPointerOver(false);
        _aheadCard = null;
        _aheadTimer?.Stop();
    }

    /// <summary>Cards as wide as fills the row exactly, rounded down so the last one never runs past the edge.</summary>
    private void OnAlbumGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Width == e.PreviousSize.Width)
            return;
        var columns = Math.Max(1, (int)((e.NewSize.Width + CardSpacing) / (MinCardWidth + CardSpacing)));
        var width = Math.Floor((e.NewSize.Width - CardSpacing * (columns - 1)) / columns);
        AlbumLayout.MaximumRowsOrColumns = columns;
        AlbumLayout.MinItemWidth = width;
        AlbumLayout.MinItemHeight = width + CardTextHeight;
    }

    /// <summary>Each card's details, then its picture if it is shown; without a size to hide by, both at once.</summary>
    private async Task FillCardsAsync(IReadOnlyList<AlbumCardViewModel> cards, CancellationToken cancel)
    {
        try
        {
            await Task.WhenAll(cards.Select(async card =>
            {
                var details = InSlotAsync(_detailSlots, () => ReadDetailsAsync(card, cancel), cancel);
                if (Shows(card))
                    await Task.WhenAll(details, InSlotAsync(_pictureSlots, () => LoadPictureAsync(card, cancel), cancel));
                else
                {
                    await details;
                    if (_shownAlbums.Contains(card))
                        await InSlotAsync(_pictureSlots, () => LoadPictureAsync(card, cancel), cancel);
                }
            }));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // other cards took their place, or the page closed
        }
    }

    private async Task ReadDetailsAsync(AlbumCardViewModel card, CancellationToken cancel)
    {
        AlbumDetails? details;
        try
        {
            details = await _app.Browse.DetailsAsync(card.Album.Link, cancel);
        }
        catch (StageException)
        {
            details = null; // the list's count stays, and the card shows whatever the size filter says
        }
        cancel.ThrowIfCancellationRequested();
        card.Read(details);
        Place(card);
        ShowAlbumsNote();
        LoadMoreIfNear(); // hiding it may have brought the end near
    }

    /// <summary>
    /// The card's picture, read where the list was: in server mode the server fetches it, and this PC fetches nothing
    /// from the site. One that can't be read leaves the card's colour and initial.
    /// </summary>
    private async Task LoadPictureAsync(AlbumCardViewModel card, CancellationToken cancel)
    {
        if (card.PictureAsked || card is not { Album.Thumbnail: { } picture, Thumbnail: { } image })
            return;
        card.PictureAsked = true;
        try
        {
            await SetPictureAsync(picture, image, cancel);
        }
        catch (OperationCanceledException)
        {
            card.PictureAsked = false; // asked again when it is shown again
            throw;
        }
    }

    /// <summary>A picture of an album or file, read where pages are, into <paramref name="image"/>; one that can't be read leaves it empty.</summary>
    /// <returns>whether it was read</returns>
    private async Task<bool> SetPictureAsync(Uri picture, BitmapImage image, CancellationToken cancel)
    {
        try
        {
            var bytes = await _app.Browse.ThumbnailAsync(picture, cancel);
            cancel.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            await image.SetSourceAsync(stream);
            return true;
        }
        catch (Exception e) when (e is StageException or COMException)
        {
            return false; // not there, too large, or not an image
        }
    }

    private static Task InSlotAsync(Slots slots, Func<Task> read, CancellationToken cancel) => slots.RunAsync(read, cancel);

    /// <summary>At most so many reads at a time, the rest waiting their turn; used on the UI thread only.</summary>
    private sealed class Slots(int count)
    {
        private readonly Queue<TaskCompletionSource> _waiting = new();
        private int _free = count;

        public async Task RunAsync(Func<Task> read, CancellationToken cancel)
        {
            if (_free > 0)
                _free--;
            else
            {
                var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting.Enqueue(turn);
                using (cancel.Register(() => turn.TrySetCanceled(cancel)))
                    await turn.Task; // a turn handed over is this one's slot
            }
            try
            {
                await read();
            }
            finally
            {
                Release();
            }
        }

        /// <summary>The slot goes to the next waiting read that hasn't been cancelled, else back to the free ones.</summary>
        private void Release()
        {
            while (_waiting.TryDequeue(out var next))
            {
                if (next.TrySetResult())
                    return;
            }
            _free++;
        }
    }

    private void OnOpenAlbumCard(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card)
            return;
        Open(card.Album.Link.Original.ToString(), card.Title, card.Album.Thumbnail);
    }

    private void OnDownloadAlbumCard(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card)
            _ = DownloadAlbumAsync(card);
    }

    // a file's round download button grows a little under the pointer, and dips when clicked; it scales about its middle
    // (CenterPoint), easing between sizes (ScaleTransition)
    private static readonly System.Numerics.Vector3 Grown = new(1.12f, 1.12f, 1);
    private static readonly System.Numerics.Vector3 Dipped = new(0.86f, 0.86f, 1);

    private void OnRoundButtonEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement button)
            button.Scale = Grown;
    }

    private void OnRoundButtonExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement button)
            button.Scale = System.Numerics.Vector3.One;
    }

    /// <summary>The button dips, then comes back to the size the pointer gives it.</summary>
    private static async void Dip(object sender)
    {
        if (sender is not Button button)
            return;
        button.Scale = Dipped;
        await Task.Delay(TimeSpan.FromMilliseconds(120));
        button.Scale = button.IsPointerOver ? Grown : System.Numerics.Vector3.One;
    }

    /// <summary>Right-click (or the menu key) on a card: open the album, or send all of it to Downloads.</summary>
    private void OnAlbumCardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (CardOf(sender) is not { } card)
            return;
        args.Handled = true;
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Open", OpenGlyph, () => Open(card.Album.Link.Original.ToString(), card.Title, card.Album.Thumbnail)));
        menu.Items.Add(MenuItem("Download album", "\uE896", () => _ = DownloadAlbumAsync(card)));
        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, point);
        else
            menu.ShowAt((FrameworkElement)sender);
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>
    /// Read the card's album where pages are and queue every file on it, into a folder named after the album, the card
    /// saying "Adding…" and then how many went; the page stays as it is.
    /// </summary>
    private async Task DownloadAlbumAsync(AlbumCardViewModel card)
    {
        if (card.Downloading)
            return;
        if (_app.Browse.Browser is null)
        {
            ShowProblem("Couldn't download the album", _app.Browse.Unavailable ?? "");
            return;
        }
        card.ShowDownload("Adding…", downloading: true);
        try
        {
            var page = await _app.Browse.OpenAsync(card.Album.Link, CancellationToken.None);
            var result = await AddToDownloadsAsync([.. page.Files.Select(file => file.Link.Original.ToString())], card.Title);
            card.ShowDownload(page.Files.Count == 0 ? "No files"
                              : result is null ? ""
                              : result.Added.Count > 0 ? $"{result.Added.Count:N0} added"
                              : "In Downloads", downloading: false, added: result is { Rejected.Count: 0 } && page.Files.Count > 0);
        }
        catch (StageException ex)
        {
            ShowAlbumNotRead(card, ex);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            CrashLog.Write("reading an album", ex);
            ShowAlbumNotRead(card, ex);
        }
    }

    private void ShowAlbumNotRead(AlbumCardViewModel card, Exception why)
    {
        card.ShowDownload("", downloading: false);
        ShowProblem("Couldn't read the album", why.Message);
    }

    /// <summary>The card a control on it belongs to.</summary>
    private AlbumCardViewModel? CardOf(object sender) => ItemOf(sender, AlbumGrid, _shownAlbums);

    /// <summary>
    /// The item a control on its card in <paramref name="repeater"/> belongs to. ItemsRepeater leaves DataContext unset on
    /// an x:Bind template, so it is found by the place in the repeater of the element the control is in.
    /// </summary>
    private static T? ItemOf<T>(object sender, ItemsRepeater repeater, IReadOnlyList<T> items) where T : class
    {
        for (var element = sender as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is UIElement card && ReferenceEquals(VisualTreeHelper.GetParent(card), repeater))
                return repeater.GetElementIndex(card) is var index and >= 0 && index < items.Count ? items[index] : null;
        }
        return null;
    }
}
