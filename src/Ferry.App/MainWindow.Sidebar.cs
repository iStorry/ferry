using Ferry.App.Services;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Plugins.RemoteQueues;
using Ferry.Plugins.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Ferry.App;

/// <summary>The sidebar: what goes on in each place, and in its foot the card with where downloads run.</summary>
public sealed partial class MainWindow
{
    // narrower than this, the sidebar is icons only
    private const double SidebarWidth = 860;
    private const double WideSidebar = 208;
    private const double CompactSidebar = 60;

    private readonly DriveRoom _room = new();
    private bool _compact;

    /// <summary>What plugins add to the window is there while they offer it: the Browser while one has a site to browse, the Library while one looks titles up.</summary>
    private void UpdatePlugins()
    {
        var browse = _app.Browse.IsOffered;
        BrowserItem.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;
        CommandBox.PlaceholderText = _app.IsDecoy ? "Paste links" : browse ? "Paste a link, search or run a command" : "Paste a link or run a command";
        AutomationProperties.SetName(CommandBox, CommandBox.PlaceholderText);
        if (!browse && BrowserItem.IsChecked == true)
            ShowDownloads();
        var library = _app.LibraryPlugin is not null;
        LibraryItem.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
        if (!library && LibraryItem.IsChecked == true)
            ShowDownloads();
        var plugins = PluginCatalog.CountOn(_app.Settings);
        PluginsCount.Text = plugins > 0 ? $"{plugins} on" : ""; // none is how it starts, and nothing to count
        AutomationProperties.SetName(PluginsItem, plugins > 0 ? $"Plugins, {plugins} on" : "Plugins");
        UpdateWatchBadge();
        UpdateAddedLabel();
    }

    private void UpdateWatchBadge()
    {
        var added = BrowserItem.Visibility == Visibility.Visible ? _app.Browse.Watcher.Albums.Sum(album => album.NewFiles) : 0;
        WatchBadge.Value = added;
        WatchBadge.Visibility = Wide(added > 0);
        WatchDot.Visibility = added > 0 && _compact ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(BrowserItem, added > 0 ? $"Browser, {added} new in watched albums" : "Browser");
    }

    /// <summary>The heading over what plugins add, while there is any; a line for it in a narrow window.</summary>
    private void UpdateAddedLabel()
    {
        var any = BrowserItem.Visibility == Visibility.Visible || LibraryItem.Visibility == Visibility.Visible || ServerItem.Visibility == Visibility.Visible;
        AddedLabel.Visibility = Wide(any);
        AddedLine.Visibility = any && _compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateUpdateLine()
    {
        var ready = _app.Updater.State == UpdateState.Available && _app.Updater.Available is not null;
        UpdateLine.Visibility = Wide(ready);
        if (_app.Updater.Available is { } release)
            UpdateText.Text = $"Ferry {release.Version.ToString(3)} is ready";
    }

    private void OnUpdateLine(object sender, RoutedEventArgs e) => ShowAbout();

    /// <summary>Whether what belongs in the wide sidebar shows: when it has something to say, and there is room.</summary>
    private Visibility Wide(bool show) => show && !_compact ? Visibility.Visible : Visibility.Collapsed;

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < SidebarWidth;
        if (compact == _compact)
            return;
        _compact = compact;
        var width = new GridLength(compact ? CompactSidebar : WideSidebar);
        (SideColumn.Width, TitleColumn.Width) = (width, width);
        TitleText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var place in new[] { DownloadsItem, BrowserItem, LibraryItem, ServerItem, PluginsItem, SettingsItem, AboutItem })
        {
            // with its word beside it, it needs no tooltip to say it again
            ToolTipService.SetToolTip(place, compact ? place.Tag : null);
            if (place.Content is not Grid row)
                continue;
            row.ColumnSpacing = compact ? 0 : 12;
            foreach (var word in row.Children.OfType<TextBlock>().Where(block => Grid.GetColumn(block) == 1))
                word.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
        PluginsCount.Visibility = Wide(true);
        WhereCard.Padding = compact ? new Thickness(0, 12, 0, 12) : new Thickness(12, 10, 12, 10);
        WhereTop.ColumnSpacing = compact ? 0 : 8;
        WhereTop.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        ModeTitle.Visibility = Wide(true);
        ModeDetail.Visibility = Wide(true);
        UpdateProgress();
        UpdateWatchBadge();
        UpdateAddedLabel();
        UpdateUpdateLine();
        UpdateModeLine();
    }

    /// <summary>
    /// The queue's progress outside the Downloads page too: on the taskbar button, and in the sidebar, as a ring around
    /// Downloads with the active count, or the files that need the user.
    /// </summary>
    private void UpdateProgress()
    {
        var items = _app.Queue.Queue?.Items ?? [];
        var fraction = QueueTotals.Fraction(items, _app.Settings.Mode == DownloadMode.Server);
        var busy = items.Any(QueueBatch.IsBusy);
        if (busy)
            Taskbar.Show(Taskbar.State.Normal, fraction);
        else if (items.Any(i => i.State == QueueState.Paused))
            Taskbar.Show(Taskbar.State.Paused, fraction);
        else
            Taskbar.Show(Taskbar.State.None);
        QueueRing.Value = fraction;
        QueueRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DownloadsGlyph.FontSize = busy ? 12 : 16;
        var active = items.Count(i => i.IsActive);
        var failed = items.Count(i => i.State == QueueState.Failed);
        DownloadsCount.Text = (failed > 0 ? failed : active).ToString(System.Globalization.CultureInfo.CurrentCulture);
        DownloadsCount.FontWeight = failed > 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        DownloadsCount.Foreground = ThemeBrushes.Get(failed > 0 ? ThemeBrushes.Critical : ThemeBrushes.Tertiary, Root.ActualTheme);
        DownloadsCount.Visibility = Wide(failed > 0 || active > 0);
        DownloadsDot.Visibility = failed > 0 && _compact ? Visibility.Visible : Visibility.Collapsed;
        var parts = new[] { active > 0 ? $"{active} active" : null, failed > 0 ? $"{failed} failed" : null }.OfType<string>();
        AutomationProperties.SetName(DownloadsItem, string.Join(", ", parts.Prepend("Downloads")));
    }

    private void OnWhereCardClick(object sender, RoutedEventArgs e) => ShowWhere();

    /// <summary>Where downloads run, up close: the server's page when there is a server to show; else where this is chosen.</summary>
    private void ShowWhere()
    {
        if (_app.Queue.Where == DownloadPlace.RemoteQueue)
            ShowPlugin(RemoteQueuesPlugin.Plugin.Name);
        else if (_app.Queue.Status is not (ModeStatus.Local or ModeStatus.NotSetUp))
            Show(ServerItem, new EntranceNavigationTransitionInfo());
        else if (_app.Settings.ServerSupportIsOn)
            ShowServerPlugin();
        else
            ShowSettings();
    }

    /// <summary>
    /// The card in the sidebar's foot: where downloads happen, how that is, and the room left there. Its icon says where,
    /// also in a narrow window, with a dot on its corner in the colour of how it is; while that is changing a spinner takes the icon's place.
    /// </summary>
    private void UpdateModeLine()
    {
        if (!_sessionStarted)
            return;
        var queue = _app.Queue;
        var (title, detail, tone) = WhereWords(queue);
        var busy = queue.Status is ModeStatus.Connecting or ModeStatus.Reconnecting or ModeStatus.Disconnecting;
        var serverMode = _app.Settings.Mode == DownloadMode.Server;
        ServerItem.Visibility = serverMode ? Visibility.Visible : Visibility.Collapsed;
        if (!serverMode && ServerItem.IsChecked == true)
            ShowDownloads(); // back to this PC: that page has nothing to show
        UpdateAddedLabel();
        ShowRoom(queue);
        ModeTitle.Text = title;
        ModeDetail.Text = detail;
        ModeIcon.Glyph = Glyphs.For(queue.Where);
        ModeDot.Fill = ThemeBrushes.Get(tone, Root.ActualTheme);
        ModeDot.Visibility = !busy && tone != ThemeBrushes.Tertiary ? Visibility.Visible : Visibility.Collapsed;
        (ModeSpinner.Visibility, ModeSpinner.IsActive) = (busy ? Visibility.Visible : Visibility.Collapsed, busy);
        ModeIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(WhereCard, WhereTip(queue, title, detail));
        // in a narrow window the detail is hidden: a screen reader still hears it
        AutomationProperties.SetName(WhereCard, $"{title}, {detail}");
    }

    /// <summary>The card's words, and the colour of its dot: the tertiary one means there is nothing to mark.</summary>
    private (string Title, string Detail, string Tone) WhereWords(QueueHost queue) => queue.Status switch
    {
        ModeStatus.Connected => (queue.WhereDisplay, "Connected", ThemeBrushes.Accent),
        ModeStatus.Connecting => (queue.WhereDisplay, "Connecting…", ThemeBrushes.Tertiary),
        ModeStatus.Reconnecting => (queue.WhereDisplay, "Reconnecting…", ThemeBrushes.Tertiary),
        ModeStatus.Disconnecting => (queue.WhereDisplay, "Disconnecting…", ThemeBrushes.Tertiary),
        ModeStatus.Offline => (queue.WhereDisplay, "Not reachable", ThemeBrushes.Critical),
        ModeStatus.NotSetUp => ("No server set up", "Set one up in Plugins", ThemeBrushes.Critical),
        _ when queue.Where != DownloadPlace.RemoteQueue => ("This PC", Folders.Shorten(_app.Settings.DownloadFolder), ThemeBrushes.Tertiary),
        _ when HasRemoteProblem(queue) => (queue.WhereDisplay, "Not reachable", ThemeBrushes.Critical),
        _ => (queue.WhereDisplay, "Remote queue", ThemeBrushes.Tertiary),
    };

    /// <summary>A remote queue says what is wrong after its name; said without, all is well.</summary>
    private static bool HasRemoteProblem(QueueHost queue) => queue.StatusDetail.Length > queue.WhereDisplay.Length;

    /// <summary>Where downloads run, in a sentence, and why that isn't working if it isn't.</summary>
    private static string WhereTip(QueueHost queue, string title, string detail)
    {
        if (queue.Status == ModeStatus.NotSetUp)
            return $"{title}. {detail}.";
        var where = queue.Where switch
        {
            DownloadPlace.Server => $"Downloads run on {queue.WhereDisplay}, then come here.",
            DownloadPlace.RemoteQueue => $"Downloads run on {queue.WhereDisplay}.",
            _ => "Downloads run on this PC.",
        };
        return queue.Status == ModeStatus.Offline || (queue.Where == DownloadPlace.RemoteQueue && HasRemoteProblem(queue))
            ? $"{where} {queue.StatusDetail}"
            : where;
    }

    /// <summary>The room left where downloads land: this PC's drive as a bar, or as the server says it. A remote queue's isn't known.</summary>
    private void ShowRoom(QueueHost queue)
    {
        long? free = null;
        double? used = null;
        if (queue.Status == ModeStatus.Local && queue.Where == DownloadPlace.ThisPc)
        {
            _room.Refresh(_app.Settings.DownloadFolder);
            (free, used) = (_room.Free, _room.Used);
        }
        else if (queue.Status == ModeStatus.Connected)
        {
            free = queue.ServerFreeBytes;
        }
        ModeSpace.Visibility = Wide(used is not null);
        ModeSpace.Value = used ?? 0;
        ModeFree.Visibility = Wide(free is not null);
        ModeFree.Text = free is { } room ? $"{Core.Text.Units.Size(room)} free" : "";
    }
}
