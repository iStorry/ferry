using System.Globalization;
using System.Text;
using Ferry.App.Services;
using Ferry.Core;
using Ferry.Core.Queue;
using Ferry.Plugins.Server;
using Ferry.Core.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Ferry.App.Pages;

/// <summary>
/// Server mode's server: what crosses from the sites to it and on to this PC, how it is doing right now (CPU, memory, disk,
/// load), the last minute's speeds, the agent, and the end of the agent's log. Asked over the agent connection already open,
/// and only while this page shows. A short drop is only a spinner: <see cref="QueueHost"/> says when it counts as offline.
/// </summary>
public sealed partial class ServerPage : Page
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);
    private const int LogEveryTicks = 2; // the log every other time: it changes less, and is larger
    private const double WideWidth = 820;
    private const double StopWidth = 96;
    private const double NarrowStopWidth = 60;
    private const double StaleOpacity = 0.5;
    private const double DiskHighShare = 0.8; // this full, the disk's bar turns amber

    private static readonly string[] ProblemWords = ["failed", "couldn't", "crashed", "unobserved"];

    private readonly AppServices _app = App.Services;
    private readonly DispatcherQueueTimer _timer;
    private readonly DriveRoom _pcRoom = new();
    private readonly Gauge _cpu;
    private readonly Gauge _memory;
    private readonly Gauge _disk;
    private readonly Gauge _load;
    private readonly Gauge[] _gauges;
    private readonly Leg _sites;
    private readonly Leg _here;
    private AgentStats? _last;
    private DateTime? _lastHeard;
    private string? _refreshProblem;
    private string _log = "";
    private int _columns;
    private int _ticks;
    private bool _busy;
    private bool _stale;

    public ServerPage()
    {
        InitializeComponent();
        _cpu = new Gauge("CPU", "\uE950", Resources);
        _memory = new Gauge("Memory", "\uE964", Resources);
        _disk = new Gauge("Disk", "\uEDA2", Resources);
        _load = new Gauge("Load", "\uEC4A", Resources);
        _gauges = [_cpu, _memory, _disk, _load];
        _sites = new Leg(SitesSpeed, SitesUnit, SitesLine, SitesFlow, SitesNote);
        _here = new Leg(HereSpeed, HereUnit, HereLine, HereFlow, HereNote);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = Every;
        _timer.Tick += (_, _) => _ = RefreshAsync(log: ++_ticks % LogEveryTicks == 0 && Follow.IsOn);
        Layout(4);
        ActualThemeChanged += (_, _) => ShowStatus();
        Loaded += (_, _) =>
        {
            _app.Queue.StatusChanged += OnStatusChanged;
            _app.Speed.Sampled += OnSpeedSampled;
            _pcRoom.Changed += ShowPcFree;
            OnStatusChanged();
            _timer.Start();
            _ = RefreshAsync(log: true);
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _app.Queue.StatusChanged -= OnStatusChanged;
            _app.Speed.Sampled -= OnSpeedSampled;
            _pcRoom.Changed -= ShowPcFree;
            _sites.Stop();
            _here.Stop();
        };
    }

    private void OnStatusChanged()
    {
        ShowStatus();
        ShowPcFree();
        if (_app.Queue.Status == ModeStatus.Connected && _stale)
            _ = RefreshAsync(log: true); // back: catch up at once
    }

    private void OnSpeedSampled()
    {
        ShowLegs();
        DrawGraph();
    }

    /// <summary>Everything that follows from how the connection stands, in the colours of the theme now.</summary>
    private void ShowStatus()
    {
        var queue = _app.Queue;
        var status = queue.Status;
        var connected = status == ModeStatus.Connected;
        var offline = status == ModeStatus.Offline;
        ServerName.Text = queue.ServerDisplay ?? "No server";
        ToolTipService.SetToolTip(ServerName, ServerName.Text);
        ShowPills(status);
        (CheckButton.Visibility, TryNowButton.Visibility) = offline ? (Visibility.Collapsed, Visibility.Visible) : (Visibility.Visible, Visibility.Collapsed);
        var problem = offline ? queue.StatusDetail : connected && _stale ? _refreshProblem : null;
        Problem.Text = problem ?? "";
        Problem.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;

        PluginTile.Show(ServerTile, ServerIcon, connected, ActualTheme);
        ServerFree.Text = connected && (_last?.DiskFreeBytes ?? queue.ServerFreeBytes) is { } free ? $"{Units.Size(free)} free" : "";
        ShowLegs();

        var fresh = connected && !_stale;
        Figures.Opacity = fresh ? 1 : StaleOpacity;
        if (!fresh)
            Updated.Text = _lastHeard is { } at ? $"Last figures from {at:t}" : "";
        foreach (var gauge in _gauges)
            gauge.Paint(ActualTheme);
        GraphLine.Stroke = ThemeBrushes.Get(connected ? ThemeBrushes.Accent : ThemeBrushes.Tertiary, ActualTheme);
        GraphKey.Fill = GraphLine.Stroke;
        GraphArea.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The facts under the server's name: connected with what it runs, on its way with a spinner, or not reachable.</summary>
    private void ShowPills(ModeStatus status)
    {
        var theme = ActualTheme;
        Pills.Children.Clear();
        Pills.Children.Add(Pill.For(status, theme));
        if (status == ModeStatus.Offline && _lastHeard is { } at)
            Pills.Children.Add(Pill.Make($"Last heard {at:t}", theme));
        if (status != ModeStatus.Connected)
            return;
        if ((_app.Queue.Queue as RemoteQueue)?.AgentVersion is { } version)
            Pills.Children.Add(Pill.Make($"Agent {Core.Product.Describe(version)}", theme));
        if (_last is { } stats)
        {
            // Linux says a lot after its version; all of it is in the tooltip
            var end = stats.System.IndexOf(" #", StringComparison.Ordinal);
            var system = Pill.Make(end > 0 ? stats.System[..end] : stats.System, theme);
            ToolTipService.SetToolTip(system, stats.System);
            Pills.Children.Add(system);
            Pills.Children.Add(Pill.Make($"Up {Units.Time(TimeSpan.FromSeconds(stats.AgentUptimeSeconds))}", theme, glyph: "\uE823"));
        }
    }

    /// <summary>The two legs of the crossing: what moves along them, or why nobody can say.</summary>
    private void ShowLegs()
    {
        var theme = ActualTheme;
        switch (_app.Queue.Status)
        {
            case ModeStatus.Connected:
                var items = _app.Queue.Queue?.Items ?? [];
                var (downloading, coming) = (items.Count(i => i.State == QueueState.Downloading), items.Count(i => i.State == QueueState.Transferring));
                ShowLeg(_sites, _app.Speed.Downloading, downloading == 0 ? "Nothing downloading" : $"{Files(downloading)} downloading");
                ShowLeg(_here, _app.Speed.ComingOver, coming == 0 ? "Nothing coming here" : $"{Files(coming)} coming here");
                break;
            case ModeStatus.Offline:
                _sites.Show("—", "", "The server keeps downloading", moving: false, theme);
                _here.Show("—", "", "Transfers resume when it answers", moving: false, theme);
                break;
            default:
                _sites.Show("…", "", "", moving: false, theme);
                _here.Show("…", "", "", moving: false, theme);
                break;
        }
    }

    private void ShowLeg(Leg leg, SpeedHistory history, string note)
    {
        var speed = history.Samples.LastOrDefault();
        var (figure, unit) = QueueSpeed.Parts(speed);
        leg.Show(figure, unit, note, moving: speed > 0, ActualTheme);
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>The room left where files land here; a drive that's gone isn't mentioned.</summary>
    private void ShowPcFree()
    {
        _pcRoom.Refresh(_app.Settings.DownloadFolder);
        PcFree.Text = _pcRoom.Free is { } free ? $"{Units.Size(free)} free" : "";
    }

    /// <summary>Ask the agent how it is doing (and for its log); one question at a time.</summary>
    private async Task RefreshAsync(bool log)
    {
        if (_busy)
            return;
        if (_app.Queue.Queue is not RemoteQueue { IsAttached: true } queue)
        {
            if (!_stale)
            {
                _stale = true;
                ShowStatus();
            }
            return;
        }
        _busy = true;
        Busy.IsActive = true;
        try
        {
            if (await queue.StatsAsync() is { } stats)
                Show(stats);
            if (log)
                ShowLog(await queue.LogAsync());
            (_stale, _refreshProblem, _lastHeard) = (false, null, DateTime.Now);
            Updated.Text = $"Updated {DateTime.Now.ToString("T", CultureInfo.CurrentCulture)}";
        }
        catch (Exception e) when (e is StageException or InvalidOperationException)
        {
            (_stale, _refreshProblem) = (true, $"{e.Message} The last figures stay meanwhile.");
        }
        finally
        {
            _busy = false;
            Busy.IsActive = false;
            ShowStatus();
        }
    }

    private void Show(AgentStats stats)
    {
        if (_last is { } before && stats.TakenAt > before.TakenAt)
        {
            var share = Math.Max(0, (stats.AgentCpuSeconds - before.AgentCpuSeconds) / (stats.TakenAt - before.TakenAt).TotalSeconds);
            _cpu.Set($"{share * 100:0.#}%", "Of one core, used by the agent", share);
        }
        else
        {
            _cpu.Set("…", "Measuring");
        }
        if (stats is { MemoryTotalBytes: { } total and > 0, MemoryAvailableBytes: { } available })
        {
            var share = (double)(total - available) / total;
            _memory.Set($"{share * 100:0}%", $"{Units.Size(total - available)} of {Units.Size(total)} used", share);
        }
        else
        {
            _memory.Set("Unknown", "The server doesn't say");
        }
        if (stats is { DiskTotalBytes: { } disk and > 0, DiskFreeBytes: { } free })
        {
            var share = (double)(disk - free) / disk;
            _disk.Set($"{share * 100:0}%", $"{Units.Size(free)} free of {Units.Size(disk)}", share, share >= DiskHighShare ? ThemeBrushes.Caution : ThemeBrushes.Tertiary);
        }
        else
        {
            _disk.Set("Unknown", "The download folder isn't there yet");
        }
        var cores = stats.Cpus == 1 ? "1 core" : $"{stats.Cpus} cores";
        if (stats is { Load1: { } one, Load5: { } five, Load15: { } fifteen })
            _load.Set($"{one:0.00}", $"1 min, {cores}", one / Math.Max(1, stats.Cpus), tip: $"1, 5 and 15 minutes: {one:0.00}, {five:0.00}, {fifteen:0.00}. {cores}");
        else
            _load.Set("Unknown", cores);

        FactUptime.Text = Units.Time(TimeSpan.FromSeconds(stats.AgentUptimeSeconds));
        FactPcs.Text = stats.Sessions == 1 ? "1, this one" : $"{stats.Sessions}, this one included";
        FactDownloads.Text = $"{stats.Downloading}, {stats.Waiting} waiting · {stats.Finished} done";
        FactMemory.Text = $"{Units.Size(stats.AgentMemoryBytes)}, {stats.AgentThreads} threads";
        FactHost.Text = stats.Host;
        ToolTipService.SetToolTip(FactHost, $"{stats.Host} · {stats.System}");
        ToolTipService.SetToolTip(FactDownloads, FactDownloads.Text);
        _last = stats;
    }

    private void ShowLog(string text)
    {
        _log = text;
        ShowLog();
    }

    /// <summary>The log as it was last heard, all of it or only what went wrong.</summary>
    private void ShowLog()
    {
        var lines = (LogProblems.IsChecked == true ? Problems(_log) : _log).TrimEnd('\n').ReplaceLineEndings("\r");
        if (lines == LogBox.Text)
            return;
        var scroller = VisualTree.Descendant<ScrollViewer>(LogBox);
        // follow the newest lines only when they were in view: someone who scrolled up to read stays where they were
        var following = scroller is null || scroller.VerticalOffset >= scroller.ScrollableHeight - 24;
        var offset = scroller?.VerticalOffset ?? 0;
        LogBox.Text = lines;
        LogBox.UpdateLayout();
        scroller?.ChangeView(null, following ? double.MaxValue : offset, null, disableAnimation: true);
    }

    /// <summary>The lines saying something went wrong. Each starts with its date; a line that doesn't is the rest of an exception, and goes with the one before.</summary>
    private static string Problems(string log)
    {
        var kept = new StringBuilder();
        var keep = false;
        foreach (var line in log.Split('\n'))
        {
            if (line.Length > 0 && char.IsAsciiDigit(line[0]))
                keep = ProblemWords.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (keep)
                kept.Append(line).Append('\n');
        }
        return kept.ToString();
    }

    private void OnLogFilter(object sender, RoutedEventArgs e)
    {
        LogBox.PlaceholderText = LogProblems.IsChecked == true ? "No problems logged" : "Nothing logged yet";
        ShowLog();
    }

    private void OnFollowToggled(object sender, RoutedEventArgs e)
    {
        if (Follow.IsOn)
            _ = RefreshAsync(log: true);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync(log: true);

    /// <summary>Ask now: connected, whether it still answers; if not, try reaching it instead of waiting for the next retry.</summary>
    private void OnCheck(object sender, RoutedEventArgs e)
    {
        if (_app.Queue.Status == ModeStatus.NotSetUp)
            App.Window?.ShowServerSetup();
        else
            _ = Dialogs.GuardAsync(XamlRoot, "Couldn't check the server", CheckAsync);
    }

    private async Task CheckAsync()
    {
        (CheckButton.IsEnabled, TryNowButton.IsEnabled) = (false, false);
        try
        {
            await _app.Queue.RetryNowAsync();
            await RefreshAsync(log: true);
        }
        finally
        {
            (CheckButton.IsEnabled, TryNowButton.IsEnabled) = (true, true);
        }
    }

    private void OnOptions(object sender, RoutedEventArgs e) => App.Window?.ShowServerPlugin();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (!ClipboardText.TryCopy(LogBox.Text.ReplaceLineEndings()))
            _ = Dialogs.ShowErrorAsync(XamlRoot, "Couldn't copy the log", ClipboardText.Held);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < PageEdges.NarrowWidth;
        Layout(e.NewSize.Width >= WideWidth ? 4 : 2);
        Body.Padding = PageEdges.For(e.NewSize.Width);
        SitesStop.Width = ServerStop.Width = PcStop.Width = narrow ? NarrowStopWidth : StopWidth;
        _sites.Fit(narrow);
        _here.Fit(narrow);
        // the chart and the agent side by side, or one over the other
        Grid.SetColumn(AgentCard, narrow ? 0 : 1);
        Grid.SetRow(AgentCard, narrow ? 1 : 0);
        SplitSecond.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Split.ColumnSpacing = narrow ? 0 : 8;
    }

    /// <summary>A leg's line reaches across it.</summary>
    private void OnLegSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid grid && grid.Children[0] is Line line)
            line.X2 = e.NewSize.Width;
    }

    /// <summary>Four figures to a row, or two in a narrow window.</summary>
    private void Layout(int columns)
    {
        if (columns == _columns)
            return;
        _columns = columns;
        Gauges.Children.Clear();
        Gauges.ColumnDefinitions.Clear();
        Gauges.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++)
            Gauges.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < (_gauges.Length + columns - 1) / columns; r++)
            Gauges.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < _gauges.Length; i++)
        {
            Grid.SetColumn(_gauges[i].Root, i % columns);
            Grid.SetRow(_gauges[i].Root, i / columns);
            Gauges.Children.Add(_gauges[i].Root);
        }
    }

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    /// <summary>The last minute's speeds, newest on the right, against the highest of them.</summary>
    private void DrawGraph()
    {
        var (width, height) = (Graph.ActualWidth, Graph.ActualHeight);
        if (width <= 0 || height <= 0)
            return;
        var speed = _app.Speed;
        var peak = Math.Max(1, Math.Max(speed.Downloading.Peak, speed.ComingOver.Peak));
        var line = QueueSpeed.Points(speed.Downloading, width, height, peak);
        GraphLine.Points = Polyline(line);
        var area = Polyline(line);
        if (area.Count > 0)
        {
            area.Add(new Windows.Foundation.Point(width, height));
            area.Add(new Windows.Foundation.Point(area[0].X, height));
        }
        GraphArea.Points = area;
        GraphHere.Points = Polyline(QueueSpeed.Points(speed.ComingOver, width, height, peak));
    }

    private static PointCollection Polyline(List<Windows.Foundation.Point> points)
    {
        var line = new PointCollection();
        foreach (var point in points)
            line.Add(point);
        return line;
    }
}
