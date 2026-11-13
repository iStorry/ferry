using Ferry.Plugins.Server;
using System.Collections.ObjectModel;
using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core.Browse;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ferry.App.Pages;

/// <summary>
/// Open an album or file and see what is on it (names, sizes, upload dates), narrow it down, and send the chosen
/// files to Downloads; watch albums for new files. Pages are read where downloads happen: on this PC, or by the
/// server in server mode. This part reads pages; BrowserPage.Filter.cs shows them, BrowserPage.Home.cs is what shows
/// with nothing open, and BrowserPage.Albums.cs and BrowserPage.AlbumCards.cs the albums the site lists there.
/// </summary>
public sealed partial class BrowserPage : Page
{
    private readonly AppServices _app = App.Services;
    private readonly Queue<string> _moreAlbums = new();
    private const string ReadFailed = "Couldn't read the page";

    // the banner's cover as shown, twice over for a sharp screen; and behind the banner, so small that scaled up it is soft
    private const int CoverPixels = 264;
    private const int BackdropPixels = 48;

    private readonly Throttle _refreshInDownloads;
    private CancellationTokenSource? _reading;

    /// <summary>The page open, or null.</summary>
    private BrowsedPage? _page;

    /// <summary>Files added to the open album since it was last looked at, when it is watched.</summary>
    private HashSet<string> _newSlugs = [];

    /// <summary>
    /// The open page's name as the card it was opened from has it (the list's), which is the album's folder name
    /// when it is downloaded whole; null when it was opened otherwise, and the page's own title is used.
    /// </summary>
    private string? _pageName;

    /// <summary>Reads of the open page's file pictures; cancelled when it closes.</summary>
    private CancellationTokenSource? _filePictures;

    public BrowserPage()
    {
        InitializeComponent();
        _refreshInDownloads = new Throttle(DispatcherQueue, RefreshInDownloads);
        SetUpFilters();
        SetUpAlbums();
        ToolTipService.SetToolTip(WatchButton, $"Check this album for new files every {IAlbumWatcher.Every.TotalMinutes:0} minutes");
        WatchEveryText.Text = $"Checked every {IAlbumWatcher.Every.TotalMinutes:0} min";
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnPageKeyDown;
        ActualThemeChanged += (_, _) => ShowFacts();
        AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPagePointerPressed), handledEventsToo: true);
        // a row's container is what a screen reader names: the file, its size and date, whether it is new
        List.ContainerContentChanging += (_, args) =>
        {
            if (args.Item is not BrowsedFileViewModel row)
                return;
            if (args.InRecycleQueue)
            {
                row.SetPointerOver(false); // scrolled away under the pointer: no leaving it to say so
                return;
            }
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, row.ToString());
            OnFileRowRealized(row);
        };
    }

    public ObservableCollection<BrowsedFileViewModel> Files { get; } = [];

    private bool ServerMode => _app.Settings.Mode == DownloadMode.Server;

    /// <summary>Album links from elsewhere (pasted on Downloads): the first opens now, the rest one at a time.</summary>
    public void OpenAlbums(IEnumerable<string> links)
    {
        foreach (var link in links)
            _moreAlbums.Enqueue(link);
        OpenNextAlbum();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _app.Queue.Changed += OnQueueChanged;
        _app.Queue.QueueReplaced += RefreshInDownloads;
        _app.Browse.Changed += OnBrowseChanged;
        FollowAlbumScrolling();
        RefreshInDownloads();
        ShowWhere();
        ShowHome();
        PinTools();
        if (App.Window?.TakePendingAlbums() is { Count: > 0 } albums)
            OpenAlbums(albums);
        else if (App.Window?.TakePendingSearch() is { Length: > 0 } search)
            SearchAlbums(search);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _app.Queue.Changed -= OnQueueChanged;
        _app.Queue.QueueReplaced -= RefreshInDownloads;
        _app.Browse.Changed -= OnBrowseChanged;
        _refreshInDownloads.Stop(); // done on coming back
        StopFollowingAlbumScrolling();
        // the page is kept (NavigationCacheMode): what it is reading carries on, for when it shows again
    }

    private void OnOpenNextAlbum(object sender, RoutedEventArgs e) => OpenNextAlbum();

    private void OpenNextAlbum()
    {
        if (_moreAlbums.TryDequeue(out var link))
            Open(link);
        var more = _moreAlbums.Count;
        MoreAlbums.Title = more == 1 ? "1 more album from Downloads" : $"{more} more albums from Downloads";
        MoreAlbums.IsOpen = more > 0;
    }

    /// <param name="title">what to call it while it is read, when it is known (an album card's name)</param>
    /// <param name="cover">its picture, when it is known (an album card's); else its first file's stands in</param>
    private void Open(string link, string? title = null, Uri? cover = null)
    {
        _aheadCard?.SetPointerOver(false); // the albums go, and with them the pointer's leaving the card
        _ = OpenAsync(link, title, cover);
    }

    /// <summary>
    /// Read the page; a link opened meanwhile replaces the one being read. One read lately, or looked ahead at, shows at
    /// once; a watched album is read again, so what is new on it shows.
    /// </summary>
    private async Task OpenAsync(string text, string? title, Uri? cover)
    {
        Problem.IsOpen = false;
        if (!_app.Browse.TryReadPage(text, out var link, out var reason))
        {
            ShowProblem(ReadFailed, reason);
            return;
        }
        if (_app.Browse.Browser is null)
        {
            ShowProblem(ReadFailed, _app.Browse.Unavailable ?? "");
            return;
        }
        _reading?.Cancel();
        using var reading = new CancellationTokenSource();
        _reading = reading;
        RememberHomePlace();
        ShowReading(link, title);
        var watched = _app.Browse.Watcher.Find(link) is not null;
        try
        {
            var page = await _app.Browse.OpenAsync(link, reading.Token, fresh: watched);
            _pageName = title;
            Show(page, cover);
            if (watched)
                await WithWatcherAsync(watcher => watcher.MarkSeenAsync(page));
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested)
        {
            return; // a newer link took over, or the page closed
        }
        catch (StageException ex)
        {
            ShowProblem(ReadFailed, ex.Message);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            CrashLog.Write("reading a page", ex);
            ShowProblem(ReadFailed, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_reading, reading))
            {
                _reading = null;
                ReadingRing.IsActive = false;
                Reading.Visibility = Visibility.Collapsed;
                ShowHome();
            }
        }
    }

    private void ShowReading(PageLink link, string? title)
    {
        ClosePage();
        var where = ServerMode ? " on the server" : "";
        ReadingText.Text = title is not null
            ? $"Reading {title}{where}…"
            : $"Reading {(link.Kind == PageKind.Album ? "album" : "file")} {link.Slug}{where}…";
        ReadingRing.IsActive = true;
        Reading.Visibility = Visibility.Visible;
        ShowHome();
        ScrollToTop(); // where the reading, and then the page or what went wrong, show
    }

    private void Show(BrowsedPage page, Uri? cover)
    {
        var seen = _app.Browse.Watcher.Find(page.Link)?.Seen;
        _newSlugs = seen is null ? [] : [.. page.Files.Select(file => file.Link.Key).Except(seen)];
        _page = page;
        _filePictures?.Dispose();
        var pictures = new CancellationTokenSource();
        _filePictures = pictures;
        _selected = [.. page.Files.Select(file => file.Link.Key)]; // all of it is what is usually wanted
        var album = page.Link.Kind == PageKind.Album;
        HeroCover.Background = AlbumCovers.Placeholder(page.Link.Slug);
        HeroInitial.Text = AlbumCovers.Initial(page.Title);
        _ = ShowCoverAsync(page, cover ?? page.Files.Select(file => file.Thumbnail).OfType<Uri>().FirstOrDefault(), pictures.Token);
        KindText.Text = $"{page.Link.Original.Host}{page.Link.Original.AbsolutePath}";
        PageTitle.Text = page.Title;
        ShowFacts();
        var folder = Folders.Shorten(_app.Settings.DownloadFolder);
        IntoText.Text = OpenAlbumFolder is { } name ? $"Into {folder}\\{FileNames.Sanitize(name)}" : $"Into {folder}";
        SearchBox.PlaceholderText = album ? "Find in this album" : "Find on this page";
        ShowWatch();
        ShowKindCounts();
        Result.Visibility = Visibility.Visible;
        Tools.Visibility = Visibility.Visible;
        ToolsSlot.Visibility = Visibility.Visible;
        Heading.Visibility = Visibility.Collapsed;
        SelectionBar.Visibility = Visibility.Visible;
        DownloadAllText.Text = page.Files.Count == 1 ? "Download" : $"Download all {page.Files.Count:N0}";
        DownloadAllButton.IsEnabled = page.Files.Count > 0;
        RefreshInDownloads(); // which of its files are queued or were downloaded, before its cards are made
        ResetFilters();
        SizeFileGrid(); // the grid's panel may have come only with the first files
    }

    /// <summary>
    /// The cover, on the banner and dimmed behind it; until it is read, or without one, the album's colour shows there.
    /// In server mode the server reads it, as it does every picture.
    /// </summary>
    private async Task ShowCoverAsync(BrowsedPage page, Uri? cover, CancellationToken cancel)
    {
        HeroPicture.Background = null;
        Backdrop.Background = AlbumCovers.Placeholder(page.Link.Slug);
        if (cover is null)
            return;
        var sharp = new BitmapImage { DecodePixelWidth = CoverPixels };
        var soft = new BitmapImage { DecodePixelWidth = BackdropPixels };
        var read = false;
        try
        {
            await InSlotAsync(_pictureSlots, async () =>
            {
                read = await SetPictureAsync(cover, sharp, cancel) && await SetPictureAsync(cover, soft, cancel);
            }, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return; // the page closed
        }
        if (!read || !ReferenceEquals(_page, page))
            return;
        HeroPicture.Background = new ImageBrush { ImageSource = sharp, Stretch = Stretch.UniformToFill };
        Backdrop.Background = new ImageBrush { ImageSource = soft, Stretch = Stretch.UniformToFill };
    }

    /// <summary>What the open page holds, as pills, with only what is new in the accent.</summary>
    private void ShowFacts()
    {
        PageFacts.Children.Clear();
        if (_page is not { } page)
            return;
        PageFacts.Children.Add(Pill.Make(page.Files.Count switch
        {
            0 => "No files on this page",
            1 => "1 file",
            var count => $"{count:N0} files",
        }, ActualTheme));
        if (page.TotalSize is { } total)
            PageFacts.Children.Add(Pill.Make(Units.Size(total), ActualTheme));
        if (_newSlugs.Count > 0)
            PageFacts.Children.Add(Pill.Make($"{_newSlugs.Count:N0} new", ActualTheme, ThemeBrushes.Accent));
        if (ServerMode)
            PageFacts.Children.Add(Pill.Make("Read by your server", ActualTheme, glyph: Glyphs.Server));
    }

    private void OnClosePage(object sender, RoutedEventArgs e) => CloseToHome();

    /// <summary>Close the page open, back to the albums where they were.</summary>
    private void CloseToHome()
    {
        if (_page is null && _reading is null)
            return;
        _reading?.Cancel();
        ClosePage();
        Problem.IsOpen = false;
        ShowHome();
        ReturnToHomePlace();
    }

    private void ClosePage()
    {
        _filePictures?.Cancel();
        _page = null;
        Files.Clear();
        _fileRows.Clear();
        Result.Visibility = Visibility.Collapsed;
        Tools.Visibility = Visibility.Collapsed;
        ToolsSlot.Visibility = Visibility.Collapsed;
        NoMatch.Visibility = Visibility.Collapsed;
        Heading.Visibility = Visibility.Visible;
        SelectionBar.Visibility = Visibility.Collapsed;
    }

    private void ShowProblem(string title, string message)
    {
        (Problem.Title, Problem.Message) = (title, message);
        Problem.IsOpen = true;
        ScrollToTop();
    }

    private void ScrollToTop() => _scroller?.ChangeView(null, 0, null, disableAnimation: true);

    private void OnQueueChanged(QueueEvent e)
    {
        if (e is not QueueItemUpdated)
            _refreshInDownloads.Ask(); // a file came or went: its "In Downloads" tag follows
    }
}
