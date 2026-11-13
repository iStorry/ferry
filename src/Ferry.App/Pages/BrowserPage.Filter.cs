using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core.Browse;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Queue;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Ferry.App.Pages;

/// <summary>The open page's files: search, filters, sorting, and which are selected to add to Downloads.</summary>
public sealed partial class BrowserPage
{
    private const double WideCover = 132;
    private const double NarrowCover = 76;

    // wide enough for the tools on one line, also with the longest size chosen
    private const double ToolsOneLineWidth = 1100;
    private const double FindWidth = 180;

    // the grid's padding is 6 short of the page's edge: its cells' margins make up the rest
    private static readonly Thickness WideGridPadding = new(26, 0, 26, 96);
    private static readonly Thickness NarrowGridPadding = new(10, 0, 10, 96);
    private static readonly Thickness WideBannerPadding = new(32, 20, 32, 22);
    private static readonly Thickness NarrowBannerPadding = new(16, 14, 16, 18);

    // the tools stop 16 short of the right edge (for the scroll bar), so their own padding has the rest of the page's edge
    private static readonly Thickness WideToolsPadding = new(32, 10, 16, 10);
    private static readonly Thickness NarrowToolsPadding = new(16, 8, 0, 8);

    private static readonly (string Label, SizeRange Range)[] Sizes =
    [
        ("Any size", SizeRange.Any),
        ("Under 10 MB", new SizeRange(null, 10 * Units.MiB)),
        ("10 MB to 100 MB", new SizeRange(10 * Units.MiB, 100 * Units.MiB)),
        ("100 MB to 1 GB", new SizeRange(100 * Units.MiB, Units.GiB)),
        ("Over 1 GB", new SizeRange(Units.GiB, null)),
    ];

    private static readonly (string Label, FileSort Sort)[] Sorts =
    [
        ("Page order", FileSort.Page),
        ("Name", FileSort.Name),
        ("Size", FileSort.Size),
        ("Upload date", FileSort.Uploaded),
    ];

    /// <summary>The order a page opens in, and Clear goes back to: the largest files first.</summary>
    private static readonly int DefaultSort = Array.FindIndex(Sorts, s => s.Sort == FileSort.Size);
    private const bool DefaultDescending = true;

    /// <summary>Which of <see cref="Sizes"/> and of <see cref="Sorts"/> is chosen.</summary>
    private int _sizeChoice;
    private int _sortChoice = DefaultSort;

    /// <summary>The slugs selected, also those a filter hides right now: they come back selected.</summary>
    private HashSet<string> _selected = [];

    /// <summary>The slugs of the files in the Downloads queue.</summary>
    private HashSet<string> _inDownloads = [];

    /// <summary>The slugs of the open page's files that were downloaded here before (<see cref="AppServices.Downloaded"/>).</summary>
    private HashSet<string> _downloaded = [];

    /// <summary>The open page's files' cards, made as each is first shown, by slug.</summary>
    private readonly Dictionary<string, BrowsedFileViewModel> _fileRows = [];

    /// <summary>The sort the files in the list are in.</summary>
    private (int Sort, bool Descending) _shownOrder;

    // the list is being refilled: its selection events are the refill's, not the user's
    private bool _rebuilding;

    // hides the note of what was just added
    private DispatcherQueueTimer? _addedNoteTimer;

    /// <summary>The kinds to pick one of; with All, which is none of these, every kind shows.</summary>
    private (RadioButton Toggle, FileKind Kind, TextBlock Count)[] KindToggles =>
    [
        (VideosToggle, FileKind.Video, VideosCount),
        (ImagesToggle, FileKind.Image, ImagesCount),
        (ArchivesToggle, FileKind.Archive, ArchivesCount),
        (OthersToggle, FileKind.Other, OthersCount),
    ];

    private FileFilter CurrentFilter => new(
        SearchBox.Text,
        KindToggles.Where(k => k.Toggle.IsChecked == true).Select(k => k.Kind).ToHashSet(),
        Sizes[_sizeChoice].Range,
        HideInDownloadsBox.IsChecked == true,
        Sorts[_sortChoice].Sort,
        DescendingToggle.IsChecked == true);

    private bool AllSelected => Files.Count > 0 && List.SelectedItems.Count == Files.Count;

    private void SetUpFilters()
    {
        ShowChoices();
        DescendingToggle.IsChecked = DefaultDescending;
    }

    /// <summary>The size and sort chosen, on their buttons and marked in their menus.</summary>
    private void ShowChoices()
    {
        ShowChoice(SizeMenu, SizeText, nameof(Sizes), [.. Sizes.Select(s => s.Label)], _sizeChoice, index => _sizeChoice = index);
        ShowChoice(SortMenu, SortText, nameof(Sorts), [.. Sorts.Select(s => s.Label)], _sortChoice, index => _sortChoice = index);
    }

    private void ShowChoice(MenuFlyout menu, TextBlock text, string group, IReadOnlyList<string> labels, int chosen, Action<int> choose)
    {
        text.Text = labels[chosen];
        menu.Items.Clear();
        foreach (var (index, label) in labels.Index())
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = $"Browser{group}", IsChecked = index == chosen };
            item.Click += (_, _) =>
            {
                choose(index);
                text.Text = label;
                Rebuild();
            };
            menu.Items.Add(item);
        }
    }

    /// <summary>How many files of each kind the page has, on the kinds; a kind it hasn't can't be picked.</summary>
    private void ShowKindCounts()
    {
        var files = _page?.Files ?? [];
        var counts = files.CountBy(file => FileKinds.Of(file.Name)).ToDictionary();
        AllCount.Text = $"{files.Count:N0}";
        foreach (var (toggle, kind, label) in KindToggles)
        {
            var count = counts.GetValueOrDefault(kind);
            label.Text = $"{count:N0}";
            toggle.IsEnabled = count > 0;
        }
    }

    private void ResetFilters()
    {
        SearchBox.Text = "";
        AllToggle.IsChecked = true;
        (_sizeChoice, _sortChoice) = (0, DefaultSort);
        ShowChoices();
        DescendingToggle.IsChecked = DefaultDescending;
        HideInDownloadsBox.IsChecked = false;
        Rebuild();
    }

    private void OnClearFilters(object sender, RoutedEventArgs e) => ResetFilters();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Rebuild();

    private void OnFilterClicked(object sender, RoutedEventArgs e) => Rebuild();

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || SearchBox.Text.Length == 0)
            return;
        e.Handled = true;
        SearchBox.Text = "";
    }

    /// <summary>Ctrl+F: the open page's file search, else the album search.</summary>
    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_page is null)
        {
            AlbumSearch.Focus(FocusState.Keyboard);
            return;
        }
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    /// <summary>Alt+Left: back to the albums.</summary>
    private void OnBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = _page is not null || _reading is not null;
        CloseToHome();
    }

    /// <summary>Esc (when nothing nearer used it) or the keyboard's Back key: back to the albums.</summary>
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key is not (VirtualKey.Escape or VirtualKey.GoBack) || (_page is null && _reading is null))
            return;
        e.Handled = true;
        CloseToHome();
    }

    /// <summary>The mouse's back button: back to the albums.</summary>
    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsXButton1Pressed || (_page is null && _reading is null))
            return;
        e.Handled = true;
        CloseToHome();
    }

    /// <summary>
    /// The files in the Downloads queue changed: the cards' tags follow in place, so the grid keeps its place; only
    /// hiding those files changes which it shows.
    /// </summary>
    private void RefreshInDownloads()
    {
        _inDownloads = [.. (_app.Queue.Queue?.Items ?? []).Select(item => item.Link.Key)];
        _downloaded = _page is { } page && _app.Downloaded is { } downloaded
            ? [.. page.Files.Select(file => file.Link.Key).Where(downloaded.Contains)]
            : [];
        foreach (var (slug, row) in _fileRows)
            row.SetHad(_inDownloads.Contains(slug), _downloaded.Contains(slug));
        if (HideInDownloadsBox.IsChecked == true)
            Rebuild();
    }

    /// <summary>
    /// Show the open page's files through the filters, with the selection they had. Each file keeps its card and its
    /// picture for as long as the page is open, and the cards that stay shown keep their place, unless the order changed.
    /// </summary>
    private void Rebuild()
    {
        if (_page is not { } page)
            return;
        // hiding the files in Downloads hides those downloaded before too
        var shown = CurrentFilter.Apply(page.Files, _inDownloads.Union(_downloaded).ToHashSet());
        List<BrowsedFileViewModel> rows = [.. shown.Select(RowOf)];
        var order = (_sortChoice, DescendingToggle.IsChecked == true);
        _rebuilding = true;
        try
        {
            if (order != _shownOrder)
                Files.Clear();
            _shownOrder = order;
            var kept = Files.ToHashSet();
            Files.MatchTo(rows);
            foreach (var row in rows.Where(row => !kept.Contains(row) && _selected.Contains(row.File.Link.Key)))
                List.SelectedItems.Add(row);
        }
        finally
        {
            _rebuilding = false;
        }
        NoMatch.Visibility = shown.Count == 0 && page.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    /// <summary>The file's one card for the open page.</summary>
    private BrowsedFileViewModel RowOf(BrowsedFile file)
    {
        var slug = file.Link.Key;
        if (!_fileRows.TryGetValue(slug, out var row))
            _fileRows[slug] = row = new BrowsedFileViewModel(file, _newSlugs.Contains(slug), _inDownloads.Contains(slug), _downloaded.Contains(slug));
        return row;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding)
            return;
        foreach (var row in e.AddedItems.OfType<BrowsedFileViewModel>())
            _selected.Add(row.File.Link.Key);
        foreach (var row in e.RemovedItems.OfType<BrowsedFileViewModel>())
            _selected.Remove(row.File.Link.Key);
        UpdateSelection();
    }

    /// <summary>"3 selected · 1.9 GB", under it "2 more hidden by the filters", and the buttons to match.</summary>
    private void UpdateSelection()
    {
        var rows = List.SelectedItems.OfType<BrowsedFileViewModel>().ToList();
        var parts = new List<string> { $"{rows.Count:N0} selected" };
        if (rows.Count > 0)
        {
            var known = Units.Size(rows.Sum(row => row.File.Size ?? 0));
            parts.Add(rows.Any(row => row.File.Size is null) ? $"{known} and files of unknown size" : known);
        }
        SelectionText.Text = string.Join(" · ", parts);
        var hidden = _selected.Count - rows.Count;
        SelectionMore.Text = $"{hidden:N0} more hidden by the filters";
        SelectionMore.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = rows.Count > 0;
        AddText.Text = rows.Count > 0 ? $"Download {rows.Count:N0}" : "Download";
        SelectAllButton.Content = AllSelected ? "Select none" : "Select all";
        SelectAllButton.IsEnabled = Files.Count > 0;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (AllSelected)
            List.SelectedItems.Clear();
        else
            List.SelectAll();
    }

    private void OnFileGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        LayoutForWidth(e.NewSize.Width);
        SizeFileGrid();
    }

    /// <summary>
    /// A narrow window: the page's edges come in, the album's actions go under its cover and name, across, and the cover
    /// and name are smaller. The tools take one line where they fit, else two, and in a narrow window three.
    /// </summary>
    private void LayoutForWidth(double width)
    {
        // the page's edges come in, an album's actions go under its cover and name, and the cover shrinks
        var narrow = width < PageEdges.NarrowWidth;
        var oneLine = width >= ToolsOneLineWidth;
        List.Padding = narrow ? NarrowGridPadding : WideGridPadding;
        Result.Margin = new Thickness(-List.Padding.Left, 0, -List.Padding.Right, 0);
        BannerBody.Padding = narrow ? NarrowBannerPadding : WideBannerPadding;
        Grid.SetRow(HeroActions, narrow ? 1 : 0);
        Grid.SetColumn(HeroActions, narrow ? 0 : 2);
        Grid.SetColumnSpan(HeroActions, narrow ? 3 : 1);
        HeroCover.Width = HeroCover.Height = narrow ? NarrowCover : WideCover;
        PageTitle.FontSize = narrow ? 22 : 32;
        PageTitle.LineHeight = narrow ? 28 : 40;
        Tools.Padding = narrow ? NarrowToolsPadding : WideToolsPadding;
        SearchHost.Width = narrow ? double.NaN : FindWidth;
        SetCell(SearchHost, 0, 0, narrow ? 3 : 1);
        SetCell(KindsHost, narrow ? 1 : 0, narrow ? 0 : 1, narrow ? 3 : 1);
        SetCell(ToolsTail, narrow ? 2 : oneLine ? 0 : 1, oneLine ? 2 : 0, oneLine ? 1 : 3);
        ToolsTail.HorizontalAlignment = oneLine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        AlbumsCaption.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetCell(FrameworkElement element, int row, int column, int columnSpan)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    /// <summary>
    /// As many cells a row as fit, as wide as fills it. A tile is its cell less its margin (6 a side), and is this much
    /// taller than its picture for its name and details under it, so the picture is 4:3.
    /// </summary>
    private void SizeFileGrid()
    {
        const double MinTile = 156;
        const double Margins = 12;
        const double TextHeight = 44;
        if (List.ItemsPanelRoot is not ItemsWrapGrid grid)
            return;
        var width = List.ActualWidth - List.Padding.Left - List.Padding.Right;
        var cell = Math.Floor(width / Math.Max(1, (int)(width / (MinTile + Margins))));
        if (cell <= 0 || cell == grid.ItemWidth)
            return;
        grid.ItemWidth = cell;
        grid.ItemHeight = Math.Ceiling((cell - Margins) * 3 / 4) + TextHeight + Margins;
    }

    private void OnToolsSizeChanged(object sender, SizeChangedEventArgs e) => ToolsSlot.Height = e.NewSize.Height;

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => PinTools();

    /// <summary>
    /// The tools show under the banner, then stop at the top of the page as it scrolls: they lie over the list, moved to
    /// the place kept for them in its header, and no higher than the list's top.
    /// </summary>
    private void PinTools()
    {
        if (_page is not null && IsLoaded)
            ToolsShift.Y = Math.Max(0, ToolsSlot.TransformToVisual(List).TransformPoint(default).Y);
    }

    private void OnFileTileEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowsedFileViewModel row)
            row.SetPointerOver(true);
    }

    private void OnFileTileExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowsedFileViewModel row)
            row.SetPointerOver(false);
    }

    /// <summary>One file into the Downloads queue, staying here.</summary>
    private void OnDownloadFile(object sender, RoutedEventArgs e)
    {
        Dip(sender);
        if ((sender as FrameworkElement)?.DataContext is BrowsedFileViewModel row)
            _ = AddToDownloadsAsync([row.File.Link.Original.ToString()], OpenAlbumFolder);
    }

    /// <summary>A row came into view: its picture is read now, a few at a time, where pages are.</summary>
    private async void OnFileRowRealized(BrowsedFileViewModel row)
    {
        if (row.PictureAsked || row is not { File.Thumbnail: { } picture, Thumbnail: { } image } || _filePictures is not { } reading)
            return;
        row.PictureAsked = true;
        try
        {
            await InSlotAsync(_pictureSlots, () => SetPictureAsync(picture, image, reading.Token), reading.Token);
        }
        catch (OperationCanceledException)
        {
            row.PictureAsked = false; // the page closed; asked again if it is opened again
        }
    }

    /// <summary>The selected files in view into the Downloads queue, staying here.</summary>
    private void OnAddToDownloads(object sender, RoutedEventArgs e) =>
        _ = AddToDownloadsAsync([.. List.SelectedItems.OfType<BrowsedFileViewModel>().Select(row => row.File.Link.Original.ToString())],
                                OpenAlbumFolder);

    /// <summary>
    /// Queue the files as if they were pasted on Downloads, without going there: the cards show "In Downloads" as the
    /// queue takes them, and a note says how many went. What can't be queued is a problem the page shows.
    /// </summary>
    /// <param name="album">the album's name: its files go into a folder of that name in the download folder</param>
    /// <returns>what the queue did, or null when nothing could be sent</returns>
    private async Task<QueueAddResult?> AddToDownloadsAsync(IReadOnlyList<string> links, string? album = null)
    {
        const string NotAdded = "Not added to Downloads";
        if (links.Count == 0)
            return null;
        if (_app.Queue.Queue is not { } queue)
        {
            ShowProblem(NotAdded, ServerMode ? "The server can't be reached right now. Try again once it is." : "Downloads isn't ready yet.");
            return null;
        }
        QueueAddResult result;
        try
        {
            result = await queue.AddAsync(links, album is null ? null : new AddOptions(Album: album));
        }
        catch (Exception ex) when (ex is StageException or InvalidOperationException)
        {
            ShowProblem(NotAdded, ex.Message);
            return null;
        }
        if (result.Rejected.Count > 0)
            ShowProblem(NotAdded, string.Join(" ", result.Rejected.Select(r => r.Reason).Distinct()));
        var added = result.Added.Count;
        ShowAdded(added switch
        {
            0 when result.Duplicates.Count > 0 => "Already in Downloads",
            0 => null,
            1 => "Added 1 file to Downloads",
            _ => $"Added {added:N0} files to Downloads",
        });
        return result;
    }

    /// <summary>
    /// The open album's folder name, which every file downloaded from it goes into, whether all of it, a selection or
    /// one file; null for a page that is not an album, whose files go into the download folder itself, as pasted links do.
    /// </summary>
    private string? OpenAlbumFolder => _page is { Link.Kind: PageKind.Album } page ? _pageName ?? page.Title : null;

    /// <summary>Every file of the open page into the Downloads queue, whatever the filters show.</summary>
    private void OnDownloadAll(object sender, RoutedEventArgs e)
    {
        if (_page is { } page)
            _ = AddToDownloadsAsync([.. page.Files.Select(file => file.Link.Original.ToString())], OpenAlbumFolder);
    }

    private void ShowAdded(string? text)
    {
        if (text is null)
            return;
        AddedText.Text = text;
        AddedNote.Visibility = Visibility.Visible;
        _addedNoteTimer ??= DispatcherQueue.CreateTimer();
        _addedNoteTimer.Interval = TimeSpan.FromSeconds(4);
        _addedNoteTimer.IsRepeating = false;
        _addedNoteTimer.Tick -= OnAddedNoteTick;
        _addedNoteTimer.Tick += OnAddedNoteTick;
        _addedNoteTimer.Start();
    }

    private void OnAddedNoteTick(DispatcherQueueTimer sender, object args) => AddedNote.Visibility = Visibility.Collapsed;
}
