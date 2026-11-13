using System.Collections.ObjectModel;
using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core.Queue;
using Ferry.Plugins.Server;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ferry.App.Pages;

/// <summary>The rows: kept up with the queue, at most four times a second, however fast the news comes.</summary>
public sealed partial class DownloadsPage
{
    // the list's width: below these, the bar's column goes, then the figures
    private const double FullWidth = 760;
    private const double CompactWidth = 560;

    // the words on the card's buttons go when there is no room, and the filter takes less
    private const double WideFilter = 168;
    private const double NarrowFilter = 96;

    private static readonly Thickness WideListPadding = new(32, 0, 32, 24);
    private static readonly Thickness NarrowListPadding = new(16, 0, 16, 16);
    private static readonly Thickness WideHeroPadding = new(20, 14, 20, 0);
    private static readonly Thickness NarrowHeroPadding = new(16, 12, 16, 0);

    private readonly Dictionary<Guid, ItemViewModel> _byId = [];
    private readonly List<QueueGroup> _groups = [.. Enum.GetValues<QueueSection>().Select(section => new QueueGroup(section))];
    private readonly ObservableCollection<QueueGroup> _shown = []; // the sections with files, in order: an empty one takes no room
    private readonly List<(QueueSection? Section, RadioButton Tab)> _tabs = [];
    private readonly OrderedDictionary<Guid, QueueEvent> _news = []; // the latest about each file, until it is shown
    private readonly DispatcherQueueTimer _newsTimer;
    private bool _newsAsked; // shown as soon as the UI is done with what is ahead of it
    private bool _statesMoved; // among the news since it was last shown, a file came, went or changed state
    private IDownloadQueue? _rowsOf; // the queue the rows were made for
    private RowWidth _rowWidth;
    private RowContext _context;
    private QueueSection? _tab; // null for all of them

    /// <summary>Another queue took over: no row is left of the last one.</summary>
    private void Rebuild()
    {
        _rowsOf = Queue;
        _news.Clear();
        foreach (var group in _groups)
            group.Clear();
        _shown.Clear();
        _byId.Clear();
        SayRowsAgain();
        CatchUp();
    }

    /// <summary>The rows as the queue has them, after a while of not hearing from it. Rows that didn't change keep their place and selection.</summary>
    private void CatchUp()
    {
        _news.Clear();
        var items = Queue?.Items ?? [];
        var known = items.Select(item => item.Id).ToHashSet();
        foreach (var gone in _byId.Values.Where(row => !known.Contains(row.Id)).ToList())
            Forget(gone);
        List<ItemViewModel> selectAgain = [];
        foreach (var item in items)
            Upsert(item, add: true, selectAgain);
        SettleRows(selectAgain);
    }

    /// <summary>
    /// Noted, to be shown with what else comes: a file that came, went or changed state as soon as the UI is free, which
    /// makes one pass of any burst of them, and progress when the next pass is due.
    /// </summary>
    private void OnQueueChanged(QueueEvent e)
    {
        _statesMoved |= e is not QueueItemUpdated;
        var id = e.Item.Id;
        var was = _news.TryGetValue(id, out var noted) ? noted : null;
        _news[id] = (was, e) switch
        {
            (QueueItemRemoved, QueueItemUpdated or QueueItemStateChanged) => was, // late news of what has gone
            (not null, QueueItemUpdated or QueueItemStateChanged) when was.Item.Revision > e.Item.Revision => was, // older than what is noted
            (QueueItemAdded, QueueItemUpdated or QueueItemStateChanged) => new QueueItemAdded(e.Item), // still new to the list
            (not null and not QueueItemRemoved, QueueItemAdded) when was.Item.Revision > e.Item.Revision => new QueueItemAdded(was.Item), // said after what came next
            _ => e,
        };
        if (_newsTimer.IsRunning || _newsAsked)
            return;
        if (e is QueueItemUpdated)
        {
            _newsTimer.Start();
            return;
        }
        _newsAsked = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ShowNews);
    }

    /// <summary>What was noted, on the rows and the totals; what comes now waits for <see cref="Throttle.Every"/>.</summary>
    private void ShowNews()
    {
        _newsAsked = false;
        if (_news.Count == 0)
            return;
        var statesMoved = _statesMoved;
        _statesMoved = false;
        List<ItemViewModel> selectAgain = [];
        foreach (var e in _news.Values)
        {
            if (e is not QueueItemRemoved)
                Upsert(e.Item, add: e is QueueItemAdded, selectAgain);
            else if (_byId.TryGetValue(e.Item.Id, out var gone))
                Forget(gone);
        }
        _news.Clear();
        SettleRows(selectAgain);
        if (statesMoved)
            UpdateTotals();
        else
            ShowFigures();
        ScrollToAdded();
        _newsTimer.Start();
    }

    /// <summary>The first of the files just added in view, once the list has it.</summary>
    private void ScrollToAdded()
    {
        if (_scrollTo is not { } first || !_byId.TryGetValue(first, out var row))
            return;
        _scrollTo = null;
        List.ScrollIntoView(row);
    }

    private void Forget(ItemViewModel row)
    {
        _byId.Remove(row.Id);
        GroupOf(row).Leave(row);
    }

    /// <param name="selectAgain">gets a row that was selected and changed section, which loses the selection</param>
    private void Upsert(QueueItem item, bool add, List<ItemViewModel> selectAgain)
    {
        if (!_byId.TryGetValue(item.Id, out var row))
        {
            if (!add)
                return; // news about a file this list doesn't have (removed meanwhile): nothing to show

            row = new ItemViewModel(item) { Source = _app.Queue.Sites?.NameOf(item.Link.SiteId) ?? "" };
            _byId[item.Id] = row;
            row.Refresh(_context);
            GroupOf(row).Enter(row);
            return;
        }
        var (was, wasIn) = (row.StateText, GroupOf(row));
        row.Update(item, _context);
        if (GroupOf(row) is var now && now != wasIn)
        {
            // a new state, a new section: the row moves, at the end of its new one, and away from the pointer
            row.Hovered = false;
            wasIn.Leave(row);
            now.Enter(row);
            if (row.Selected)
                selectAgain.Add(row);
        }
        else if (row.StateText != was && List.ContainerFromItem(row) is ListViewItem container)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(container, row.ToString());
        }
    }

    /// <summary>The rows that came, went and moved, each section arranged once, and those that moved still selected if they are still shown.</summary>
    private void SettleRows(List<ItemViewModel> selectAgain)
    {
        foreach (var group in _groups)
            group.Settle();
        ShowSections();
        foreach (var row in selectAgain.Where(row => _shown.Contains(GroupOf(row)) && GroupOf(row).Contains(row)))
            List.SelectedItems.Add(row);
    }

    private QueueGroup GroupOf(ItemViewModel row) => _groups[(int)row.Section];

    /// <summary>The sections with files, as the tab chosen asks. Nothing can be retried until the server answers, so what needs you waits until then.</summary>
    private void ShowSections()
    {
        var offline = _app.Queue.Status == ModeStatus.Offline;
        foreach (var group in _groups)
        {
            var wanted = group.Count > 0 && (_tab is null || _tab == group.Section) && !(offline && group.Section == QueueSection.NeedsYou);
            if (!wanted)
                _shown.Remove(group);
            else if (!_shown.Contains(group))
                _shown.Insert(_shown.Count(shown => shown.Section < group.Section), group);
        }
    }

    /// <summary>Every row says itself again, if what they all depend on changed.</summary>
    private void SayRowsAgain()
    {
        var now = CurrentContext();
        if (now != _context)
            SayRows(now);
    }

    private void SayRows(RowContext context)
    {
        _context = context;
        foreach (var row in _byId.Values)
            row.Refresh(context);
    }

    /// <summary>High contrast went on or off, or to other colours: what the rows were drawn in is gone, whatever else stays.</summary>
    private void OnContrastChanged()
    {
        ItemViewModel.ForgetBrushes();
        if (!_onScreen)
            return; // coming back says them again, as the accent they depend on changed too
        SayRows(CurrentContext());
        UpdateStatus();
    }

    private RowContext CurrentContext()
    {
        var (queue, settings) = (_app.Queue, _app.Settings);
        var folder = settings.DownloadFolder;
        return new RowContext(queue.Where, queue.WhereDisplay, OnServer && queue.Status == ModeStatus.Offline, folder,
                              OnServer ? settings.Text(ServerKeys.ServerFolder) : folder, settings.Schedule,
                              ThemeBrushes.Get(ThemeBrushes.Accent, ActualTheme), ActualTheme, _rowWidth);
    }

    /// <summary>A tab for all of it and for each section, each with its count.</summary>
    private void BuildTabs()
    {
        var style = (Style)Application.Current.Resources["SegmentItemStyle"];
        foreach (var (section, words) in new (QueueSection?, string)[]
        {
            (null, "All"), (QueueSection.Now, "Now"), (QueueSection.NeedsYou, "Needs you"), (QueueSection.UpNext, "Up next"), (QueueSection.Stopped, "Paused"), (QueueSection.Done, "Done"),
        })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = words });
            row.Children.Add(new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var tab = new RadioButton { Style = style, GroupName = "QueueTabs", Content = row, IsChecked = section is null };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tab, words);
            tab.Click += (_, _) => ChooseTab(section);
            _tabs.Add((section, tab));
            Tabs.Children.Add(tab);
        }
    }

    private void ChooseTab(QueueSection? section)
    {
        _tab = section;
        _tabs.First(entry => entry.Section == section).Tab.IsChecked = true;
        ShowSections();
    }

    /// <summary>Each tab's count; one that needs you stands out.</summary>
    private void ShowTabCounts()
    {
        var total = _groups.Sum(group => group.Rows.Count);
        foreach (var (section, tab) in _tabs)
        {
            var count = section is { } one ? _groups[(int)one].Rows.Count : total;
            var number = (TextBlock)((StackPanel)tab.Content).Children[1];
            number.Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            var alert = section == QueueSection.NeedsYou && count > 0;
            number.Foreground = ThemeBrushes.Get(alert ? ThemeBrushes.Critical : ThemeBrushes.Tertiary, ActualTheme);
            number.FontWeight = alert ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            // a tab with nothing in it stays only while it is the one chosen, so the row doesn't jump as files move on
            tab.Visibility = section is null || count > 0 || _tab == section ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        foreach (var group in _groups)
            group.Filter = FilterBox.Text.Trim();
        ShowSections(); // one with no match takes no room
        ShowSummaries(); // a head that comes back has no figures yet
    }

    private void OnRowEntered(object sender, PointerRoutedEventArgs e) => SetHovered(sender, true);

    private void OnRowExited(object sender, PointerRoutedEventArgs e) => SetHovered(sender, false);

    /// <summary>The pointer can leave a row that has just been given back to the list, and so has no file.</summary>
    private static void SetHovered(object sender, bool hovered)
    {
        if (sender is FrameworkElement { DataContext: ItemViewModel row })
            row.Hovered = hovered;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ShowSelection(); // which fits the rows too
    }

    /// <summary>The rows, as the list has room, with a file's panel beside it or not.</summary>
    private void FitRows()
    {
        var listWidth = ActualWidth - PanelColumn.Width.Value;
        if (listWidth <= 0)
            return;
        var width = listWidth >= FullWidth ? RowWidth.Full : listWidth >= CompactWidth ? RowWidth.NoBarColumn : RowWidth.Compact;
        var tight = width != RowWidth.Full;
        List.Padding = width == RowWidth.Compact ? NarrowListPadding : WideListPadding;
        HeroBody.Padding = width == RowWidth.Compact ? NarrowHeroPadding : WideHeroPadding;
        // the words on the card's buttons go when there is no room, as their icons and tips say it too
        var words = tight ? Visibility.Collapsed : Visibility.Visible;
        WhenDoneText.Visibility = LimitText.Visibility = PauseAllText.Visibility = ResumeAllText.Visibility = words;
        FilterBox.Width = width == RowWidth.Compact ? NarrowFilter : WideFilter;
        if (width != _rowWidth)
        {
            _rowWidth = width;
            SayRowsAgain();
            UpdateTotals();
        }
    }
}
