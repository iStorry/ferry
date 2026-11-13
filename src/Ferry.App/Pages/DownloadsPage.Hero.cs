using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core.Queue;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ferry.App.Pages;

/// <summary>The top of the page: where downloads run, how it is going, and what to do with no files or no server.</summary>
public sealed partial class DownloadsPage
{
    // the graph's row: no room is kept for a graph until something has moved
    private const double GraphHeight = 64;
    private const double NoGraphHeight = 14;

    // the hero's big line, as a speed and as words
    private const double SpeedSize = 48;
    private const double WordsSize = 32;

    // with nothing moving, what the hero says in place of a speed of nothing
    private string? _heroWords;
    private string _factsShown = "";
    private Windows.UI.Color _graphColor;

    private void UpdateStatus()
    {
        var status = _app.Queue.Status;
        SayRowsAgain();
        ShowWhere();
        OpenBrowserButton.Visibility = _app.Browse.IsOffered ? Visibility.Visible : Visibility.Collapsed;
        ShowLimit();
        ShowEmptyKinds();
        ServerBar.IsOpen = status == ModeStatus.NotSetUp;
        QueueUnreadBar.IsOpen = _app.Queue.QueueUnread;
        var offline = status == ModeStatus.Offline;
        OfflineHero.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        OfflineWho.Text = $"{_app.Queue.WhereDisplay} can't be reached";
        OfflineDetail.Text = offline ? $"The server keeps downloading. What it finishes comes here once it answers. {_app.Queue.StatusDetail}" : "";
        ShowSections();
        UpdateTotals();
    }

    /// <summary>The hero's line on where downloads run, true for each place; it stays in sight with the server offline.</summary>
    private void ShowWhere()
    {
        var (where, machine) = (_context.Where, _context.WhereDisplay);
        var folder = _context.DownloadFolder;
        var (line, whole) = where switch
        {
            DownloadPlace.Server => ($"On {machine}, then here to {Folders.Shorten(folder)}", $"Downloaded on {machine}, then brought to {folder}"),
            DownloadPlace.RemoteQueue => ($"On {machine}", $"Downloaded by {machine}. The files stay there."),
            _ => ($"To {Folders.Shorten(folder)}", $"Downloaded to {folder}"),
        };
        HeroIcon.Glyph = Glyphs.For(where);
        HeroWhere.Text = OfflineWhere.Text = line;
        ToolTipService.SetToolTip(HeroWhere, whole);
        ToolTipService.SetToolTip(OfflineWhere, whole);
        HeroFolder.Visibility = where == DownloadPlace.RemoteQueue ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The queue as a whole: what is shown for how it stands, and the figures.</summary>
    private void UpdateTotals()
    {
        var items = AllItems();
        var states = items.CountBy(item => item.State).ToDictionary();
        ShowQueueState(items, states);
        ShowFigures(items, states);
    }

    /// <summary>What only changes when a file comes, goes or changes state.</summary>
    private void ShowQueueState(List<QueueItem> items, Dictionary<QueueState, int> states)
    {
        bool Has(QueueState state) => states.ContainsKey(state);
        if (items.Count == 0)
        {
            // in server mode an empty list may only mean the queue hasn't come from the server yet
            (EmptyTitle.Text, EmptyDetail.Text) = OnServer && _app.Queue.Status == ModeStatus.NotSetUp
                ? ("No server yet", "Links you add now wait until one is set up.")
                : OnServer && Queue is null
                ? ("Getting your queue from the server", "It shows here once the server answers; the server keeps downloading meanwhile.")
                : _app.Browse.IsOffered
                    ? ("Paste, drop or browse", "Paste links in the box at the top (Ctrl+K), drop links or a .txt of them here, or find albums in the Browser.")
                    : ("Paste or drop", "Paste links in the box at the top (Ctrl+K), or drop links or a .txt of them here.");
        }
        // only the action that applies
        var canPause = Has(QueueState.Waiting) || Has(QueueState.Resolving) || Has(QueueState.Downloading) || Has(QueueState.OnServer) || Has(QueueState.Transferring);
        PauseAllButton.Visibility = canPause ? Visibility.Visible : Visibility.Collapsed;
        ResumeAllButton.Visibility = !canPause && Has(QueueState.Paused) ? Visibility.Visible : Visibility.Collapsed;
        var offline = _app.Queue.Status == ModeStatus.Offline;
        EmptyState.Visibility = items.Count == 0 && !offline ? Visibility.Visible : Visibility.Collapsed;
        Hero.Visibility = items.Count == 0 || offline ? Visibility.Collapsed : Visibility.Visible;
        QueueTools.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowTabCounts();
        // the schedule holds what runs on this PC: the downloads here, or in server mode the transfers from the server
        var held = _context is { Where: not DownloadPlace.RemoteQueue, Schedule: { } schedule } && !schedule.IsOpen(TimeOnly.FromDateTime(DateTime.Now)) ? _context.Schedule : null;
        var moving = Has(QueueState.Resolving) || Has(QueueState.Downloading) || Has(QueueState.Transferring);
        _heroWords = moving || Has(QueueState.OnServer) ? null
            : Has(QueueState.Waiting) ? held is { } hours && !OnServer ? $"Waiting until {hours.Start:HH:mm}" : "Waiting"
            : Has(QueueState.Paused) || Has(QueueState.Cancelled) ? "Paused"
            : Has(QueueState.Failed) ? "Needs you"
            : "All done";
        HeroNote.Text = OnServer && canPause && held is { } opens ? $"The server keeps downloading. Files come here from {opens.Start:HH:mm}." : "";
        HeroNote.Visibility = HeroNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<QueueItem> AllItems() => [.. _byId.Values.Select(row => row.Item)];

    /// <summary>The figures of what is going on, for progress alone.</summary>
    private void ShowFigures()
    {
        var items = AllItems();
        ShowFigures(items, items.CountBy(item => item.State).ToDictionary());
    }

    private void ShowFigures(List<QueueItem> items, Dictionary<QueueState, int> states)
    {
        if (Panel.Visibility == Visibility.Visible && Panel.DataContext is ItemViewModel inPanel)
            ShowPanel(inPanel);
        ShowSpeed();
        ShowFacts(items, states);
        ShowSummaries();
    }

    /// <summary>The heads of the albums and the Done strip.</summary>
    private void ShowSummaries()
    {
        var byAlbum = _byId.Values.Where(row => row.HasAlbum).ToLookup(row => row.Album, StringComparer.Ordinal);
        var local = _context.Where != DownloadPlace.RemoteQueue;
        var place = local ? $"In {Folders.Shorten(_context.DownloadFolder)}" : $"On {_context.WhereDisplay}";
        foreach (var group in _groups)
            group.RefreshSummaries(byAlbum, place, local, _rowWidth);
    }

    /// <summary>What the queue is doing, as the hero's pills; only drawn again when it says something new.</summary>
    private void ShowFacts(List<QueueItem> items, Dictionary<QueueState, int> states)
    {
        int Count(QueueState state) => states.GetValueOrDefault(state);
        var theme = ActualTheme;
        var active = items.Where(item => item.IsActive).ToList();
        var speed = active.Sum(item => double.IsFinite(item.BytesPerSecond) ? item.BytesPerSecond : 0);
        var remaining = active.Sum(item => Math.Max(0, (item.Total ?? 0) - item.Done));
        var timeLeft = remaining > 0 ? Units.TimeLeft(remaining, speed) : null;
        var facts = new List<(string Figure, string Words)>();
        if (timeLeft is { } soon)
            facts.Add((Units.Time(soon), "about left"));
        if (remaining > 0)
            facts.Add((Units.Size(remaining), "to go"));
        if (OnServer)
        {
            var onServer = Count(QueueState.Downloading) + Count(QueueState.Resolving);
            var ready = Count(QueueState.OnServer);
            var coming = Count(QueueState.Transferring);
            if (onServer > 0)
                facts.Add(($"{onServer}", "on the server"));
            if (ready > 0)
                facts.Add(($"{ready}", "waiting to come here"));
            if (coming > 0)
                facts.Add(($"{coming}", "coming here"));
        }
        else if (active.Count > 0)
        {
            facts.Add(($"{active.Count} of {items.Count}", "downloading"));
        }
        if (active.Count == 0 && Count(QueueState.Waiting) is var waiting and > 0)
            facts.Add(($"{waiting}", "waiting"));
        if (facts.Count == 0)
            facts.Add(($"{Count(QueueState.Done) + Count(QueueState.Skipped)} of {items.Count}", "done"));
        var shown = string.Join("|", facts.Select(f => $"{f.Figure}/{f.Words}")) + theme;
        if (shown == _factsShown)
            return;
        _factsShown = shown;
        HeroFacts.Children.Clear();
        foreach (var (figure, words) in facts)
        {
            var fact = new StackPanel();
            fact.Children.Add(new TextBlock { Text = figure, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            fact.Children.Add(new TextBlock { Text = words, Style = (Style)Application.Current.Resources["CaptionMutedStyle"], TextWrapping = TextWrapping.NoWrap });
            HeroFacts.Children.Add(fact);
        }
    }

    private void OnSpeedSampled()
    {
        ShowSpeed();
        DrawGraph();
    }

    /// <summary>The hero's big line: how fast, or with nothing moving, what the queue is doing instead.</summary>
    private void ShowSpeed()
    {
        var speed = _app.Speed;
        (HeroSpeed.Text, HeroUnit.Text) = _heroWords is { } words ? (words, "") : QueueSpeed.Parts(speed.Downloading.Samples.LastOrDefault());
        HeroSpeed.FontSize = _heroWords is null ? SpeedSize : WordsSize;
        // server mode: the speed coming here, beside the speed on the server
        var here = OnServer && _heroWords is null;
        (HereSpeed.Text, HereUnit.Text) = here ? QueueSpeed.Parts(speed.ComingOver.Samples.LastOrDefault()) : ("", "");
        var lane = here ? Visibility.Visible : Visibility.Collapsed;
        (HereLane.Visibility, HereArrow.Visibility, ThereLabel.Visibility) = (lane, lane, lane);
    }

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    /// <summary>The last minute's speeds, newest on the right, against the highest of them.</summary>
    private void DrawGraph()
    {
        var speed = _app.Speed;
        var moved = speed.Downloading.Peak > 0 || (OnServer && speed.ComingOver.Peak > 0);
        GraphRow.Height = new GridLength(moved ? GraphHeight : NoGraphHeight);
        Graph.Visibility = moved ? Visibility.Visible : Visibility.Collapsed;
        var (width, height) = (Graph.ActualWidth, Graph.ActualHeight);
        if (width <= 0 || height <= 0)
            return;
        InkGraph();
        var peak = Math.Max(1, Math.Max(speed.Downloading.Peak, OnServer ? speed.ComingOver.Peak : 0));
        var downloading = QueueSpeed.Points(speed.Downloading, width, height, peak);
        GraphLine.Data = Curve(downloading, null);
        GraphArea.Data = Curve(downloading, height);
        GraphHere.Data = OnServer ? Curve(QueueSpeed.Points(speed.ComingOver, width, height, peak), null) : null;
        GraphHere.Visibility = OnServer ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The graph in the accent, fading to nothing before the card's lower corners.</summary>
    private void InkGraph()
    {
        var accent = ((SolidColorBrush)ThemeBrushes.Get(ThemeBrushes.Accent, ActualTheme)).Color;
        if (accent == _graphColor && GraphArea.Fill is not null)
            return;
        _graphColor = accent;
        GraphLine.Stroke = new SolidColorBrush(accent);
        GraphArea.Fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(0x42, accent.R, accent.G, accent.B), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, accent.R, accent.G, accent.B), Offset = 1 },
            },
        };
    }

    /// <summary>A smooth line through the points; closed down to <paramref name="floor"/> when it is an area.</summary>
    private static PathGeometry? Curve(List<Windows.Foundation.Point> points, double? floor)
    {
        if (points.Count < 2)
            return null;
        var figure = new PathFigure { StartPoint = points[0], IsClosed = floor is not null };
        for (var i = 1; i < points.Count; i++)
        {
            var middle = (points[i - 1].X + points[i].X) / 2;
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Windows.Foundation.Point(middle, points[i - 1].Y),
                Point2 = new Windows.Foundation.Point(middle, points[i].Y),
                Point3 = points[i],
            });
        }
        if (floor is { } bottom)
        {
            figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(points[^1].X, bottom) });
            figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(points[0].X, bottom) });
        }
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>The choice of what to do when the queue finishes, marked as it stands.</summary>
    private void ShowWhenDone()
    {
        WhenDoneMenu.Items.Clear();
        foreach (var action in Enum.GetValues<WhenDone>())
        {
            var item = new RadioMenuFlyoutItem { Text = WhenDoneActions.Label(action), GroupName = nameof(WhenDone), IsChecked = action == _app.Queue.WhenDone };
            item.Click += (_, _) => _app.Queue.WhenDone = action;
            WhenDoneMenu.Items.Add(item);
        }
        WhenDoneText.Text = _app.Queue.WhenDone == WhenDone.Nothing ? "When done" : $"Then: {WhenDoneActions.InSentence(_app.Queue.WhenDone)}";
    }

    /// <summary>The speed limit for everything, with some to pick and a way to give another.</summary>
    private void ShowLimit()
    {
        LimitText.Text = SpeedLimits.Label(_app.Settings);
        LimitButton.IsEnabled = !SpeedLimits.IsLocked(_app.Settings);
        LimitMenu.Items.Clear();
        var current = _app.Settings.SpeedLimit;
        foreach (var (words, value, bytes) in new (string, string, long)[]
        {
            ("No limit", "0", 0), ("1 MB/s", "1M", Units.MiB), ("5 MB/s", "5M", 5 * Units.MiB), ("10 MB/s", "10M", 10 * Units.MiB), ("25 MB/s", "25M", 25 * Units.MiB), ("50 MB/s", "50M", 50 * Units.MiB),
        })
        {
            var item = new RadioMenuFlyoutItem { Text = words, GroupName = "SpeedLimit", IsChecked = bytes == current };
            item.Click += async (_, _) => await SpeedLimits.SetAsync(XamlRoot, _app, value);
            LimitMenu.Items.Add(item);
        }
        LimitMenu.Items.Add(new MenuFlyoutSeparator());
        var other = new MenuFlyoutItem { Text = "Another…" };
        other.Click += async (_, _) => await SpeedLimits.AskAsync(XamlRoot, _app);
        LimitMenu.Items.Add(other);
    }

    /// <summary>What it can read now: plain links, and each plugin for links that is on.</summary>
    private void ShowEmptyKinds()
    {
        EmptyKinds.Children.Clear();
        EmptyKinds.Children.Add(Pill.Make("Any file link", ActualTheme));
        foreach (var name in PluginCatalog.LinkReaders(_app.Settings))
            EmptyKinds.Children.Add(Pill.Make(name, ActualTheme));
    }

    private async void OnRetryServer(object sender, RoutedEventArgs e) => await ServerRetry.RunAsync(XamlRoot, _app);

    private void OnOpenBrowser(object sender, RoutedEventArgs e) => App.Window?.ShowBrowser();

    private void OnFocusCommandBox(object sender, RoutedEventArgs e) => App.Window?.FocusCommandBox();

}
