using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core.Queue;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ferry.App.Pages;

/// <summary>What is selected: one file up close in the panel, or more in the bar over the page's bottom edge.</summary>
public sealed partial class DownloadsPage
{
    // the page's width: below this, there is no room for a file's panel
    private const double PanelFits = 900;
    private const double PanelWidth = 320;

    private const double FactSize = 13;

    private readonly List<PanelFact> _panelFacts = [];
    private (ItemViewModel? Row, QueueItem? Item, RowContext? Context) _panelShown;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var row in e.AddedItems.OfType<ItemViewModel>())
            row.Selected = true;
        foreach (var row in e.RemovedItems.OfType<ItemViewModel>())
            row.Selected = false;
        ShowSelection();
    }

    /// <summary>One file: the panel, when there is room. More: the bar over the page's bottom edge.</summary>
    private void ShowSelection()
    {
        var selected = List.SelectedItems.OfType<ItemViewModel>().ToList();
        var panel = selected.Count == 1 && ActualWidth >= PanelFits;
        PanelColumn.Width = new GridLength(panel ? PanelWidth : 0);
        Panel.Visibility = panel ? Visibility.Visible : Visibility.Collapsed;
        if (panel)
            ShowPanel(selected[0]);
        FitRows();
        SelectionBar.Visibility = selected.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (selected.Count > 1)
        {
            SelectionWords.Text = $"{selected.Count} selected";
            var left = selected.Sum(row => Math.Max(0, (row.Item.Total ?? 0) - row.Item.Done));
            SelectionDetail.Text = left > 0 ? $"{Units.Size(left)} left on them" : "All finished";
            SelectionPause.Text = selected.All(row => row.CanResume) ? "Resume" : "Pause";
        }
    }

    /// <summary>The file up close: how it stands, and what there is to know about it. Nothing is touched unless the file or the words for it changed.</summary>
    private void ShowPanel(ItemViewModel row)
    {
        if (_panelShown == (row, row.Item, _context))
            return;
        _panelShown = (row, row.Item, _context);
        if (!ReferenceEquals(Panel.DataContext, row))
            Panel.DataContext = row;
        PanelGlyph.Glyph = row.KindGlyph;
        PanelGlyph.Foreground = row.KindBrush;
        PanelTile.Background = row.KindBackground;
        PanelName.Text = row.Name;
        PanelState.Text = string.Join(" · ", new[] { row.StateText, row.FigureBottom == row.StateText ? "" : row.FigureBottom, row.FigureTop }.Where(words => words.Length > 0));
        PanelBar.Visibility = row.ShowBar || row.ShowSteps ? Visibility.Visible : Visibility.Collapsed;
        (PanelBar.Value, PanelBar.ShowPaused, PanelBar.IsIndeterminate) = (row.Progress, row.Paused, row.Indeterminate);
        PanelPause.Content = row.CanResume ? "Resume" : row.CanRetry ? "Retry" : "Pause";
        PanelPause.IsEnabled = row.CanPause || row.CanResume || row.CanRetry;
        PanelFolder.IsEnabled = _context.Where != DownloadPlace.RemoteQueue;
        ShowPanelFacts(FactsOf(row));
    }

    /// <summary>What there is to know about the file, each with its whole value when that is more than it shows.</summary>
    private List<(string Name, string Value, string? Whole)> FactsOf(ItemViewModel row)
    {
        var item = row.Item;
        var facts = new List<(string Name, string Value, string? Whole)> { ("Downloads on", _context.WhereDisplay, null) };
        if (row.Source.Length > 0)
            facts.Add(("Read by", row.Source, null));
        var link = item.Link.Original.ToString();
        facts.Add(("Link", item.Link.Original.Host.Length > 0 ? $"{item.Link.Original.Host}{item.Link.Original.AbsolutePath}" : link, link));
        if (FolderFact(row) is { } folder)
            facts.Add(folder);
        if (item.Total is { } total)
            facts.Add(("Size", Units.Size(total), null));
        if (item.Connections > 0)
            facts.Add(("Connections", item.Connections.ToString(System.Globalization.CultureInfo.CurrentCulture), null));
        if (item.Retries > 0)
            facts.Add(("Retries", item.Retries.ToString(System.Globalization.CultureInfo.CurrentCulture), null));
        facts.Add(("Speed limit", item.SpeedLimit > 0 ? Units.Speed(item.SpeedLimit) : "None", null));
        if (item.Sha256 is { } sha)
            facts.Add(("SHA-256", item.State == QueueState.Done ? "Checked" : "Checked when it ends", sha));
        if (item.Error is { } error)
            facts.Add(("Problem", error, error));
        return facts;
    }

    /// <summary>
    /// Where the file is or will be. Before it comes here, a file on the server lands in the download folder; on a remote queue
    /// it stays on that machine, as a path of its own; here, as the queue says.
    /// </summary>
    private (string Name, string Value, string? Whole)? FolderFact(ItemViewModel row)
    {
        var item = row.Item;
        if (_context.Where == DownloadPlace.RemoteQueue)
            return (item.Path ?? item.Folder) is { Length: > 0 } there ? ("Saved there", there, there) : null;
        if (item.Path is { } path && (_context.Where == DownloadPlace.ThisPc || ItemViewModel.OnThisPc(item)))
            return ("Saved to", Folders.Shorten(Path.GetDirectoryName(path) ?? item.Folder), path);
        var folder = _context.Where == DownloadPlace.ThisPc ? item.Folder
            : row.HasAlbum ? Path.Combine(_context.DownloadFolder, row.Album)
            : _context.DownloadFolder;
        return ("Saves to", Folders.Shorten(folder), folder);
    }

    /// <summary>The facts on the rows already there, with rows added or taken away only when their number changed.</summary>
    private void ShowPanelFacts(List<(string Name, string Value, string? Whole)> facts)
    {
        while (_panelFacts.Count > facts.Count)
        {
            var last = _panelFacts[^1];
            PanelFacts.Children.Remove(last.Label);
            PanelFacts.Children.Remove(last.Shown);
            PanelFacts.RowDefinitions.RemoveAt(PanelFacts.RowDefinitions.Count - 1);
            _panelFacts.RemoveAt(_panelFacts.Count - 1);
        }
        while (_panelFacts.Count < facts.Count)
        {
            var fact = new PanelFact(_panelFacts.Count);
            PanelFacts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PanelFacts.Children.Add(fact.Label);
            PanelFacts.Children.Add(fact.Shown);
            _panelFacts.Add(fact);
        }
        var ink = ThemeBrushes.Get(ThemeBrushes.Secondary, ActualTheme);
        foreach (var (at, (name, value, whole)) in facts.Index())
            _panelFacts[at].Show(name, value, whole, ink);
    }

    private async void OnPanelPause(object sender, RoutedEventArgs e)
    {
        var row = Row(sender);
        await RunAsync(q => row.CanResume ? q.ResumeAsync(row.Id) : row.CanRetry ? q.RetryAsync(row.Id) : q.PauseAsync(row.Id));
    }

    private async void OnPauseSelected(object sender, RoutedEventArgs e)
    {
        var selected = List.SelectedItems.OfType<ItemViewModel>().ToList();
        var resume = selected.All(row => row.CanResume);
        await RunAllAsync([.. selected.Where(row => resume || row.CanPause)], (q, row) => resume ? q.ResumeAsync(row.Id) : q.PauseAsync(row.Id));
    }

    private async void OnRemoveSelected(object sender, RoutedEventArgs e) => await RemoveAsync([.. List.SelectedItems.OfType<ItemViewModel>()]);

    private void OnClearSelection(object sender, RoutedEventArgs e) => List.SelectedItems.Clear();

    /// <summary>A row of the panel's details, its name beside its value; only what changed is set on them.</summary>
    private sealed class PanelFact
    {
        private string _name = "";
        private string _value = "";
        private string? _whole;
        private Brush? _ink;

        public PanelFact(int row)
        {
            Grid.SetRow(Label, row);
            Grid.SetRow(Shown, row);
            Grid.SetColumn(Shown, 1);
        }

        public TextBlock Label { get; } = new() { FontSize = FactSize };

        public TextBlock Shown { get; } = new() { FontSize = FactSize, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };

        public void Show(string name, string value, string? whole, Brush ink)
        {
            if (name != _name)
                Label.Text = _name = name;
            if (value != _value)
                Shown.Text = _value = value;
            if (whole != _whole)
                ToolTipService.SetToolTip(Shown, _whole = whole);
            if (!ReferenceEquals(ink, _ink))
                Label.Foreground = _ink = ink;
        }
    }
}
