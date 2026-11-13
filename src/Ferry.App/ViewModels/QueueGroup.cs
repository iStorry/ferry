using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ferry.Core.Queue;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ferry.App.ViewModels;

/// <summary>
/// A section of the Downloads page (docs/mocks/downloads.html): its files, under a header with their count. In Now,
/// an album's files show together under its head; in Done, a strip sums them up, with the files themselves on request.
/// </summary>
public sealed class QueueGroup(QueueSection section) : ObservableCollection<object>
{
    private readonly List<ItemViewModel> _rows = [];
    private readonly HashSet<ItemViewModel> _leaving = [];
    private readonly Dictionary<string, AlbumGroupViewModel> _albums = new(StringComparer.Ordinal);
    private bool _unsettled;
    private bool _stripStale = true;

    public QueueSection Section { get; } = section;

    public string Title { get; } = section switch
    {
        QueueSection.NeedsYou => "Needs you",
        QueueSection.Now => "Now",
        QueueSection.UpNext => "Up next",
        QueueSection.Stopped => "Paused",
        _ => "Done",
    };

    public string CountText => _rows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Only files with this in their name show; "" for all.</summary>
    public string Filter
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Arrange();
        }
    } = "";

    public IReadOnlyList<ItemViewModel> Rows => _rows;

    /// <summary>The Done section's summary; null in the others.</summary>
    public DoneStripViewModel? Strip { get; } = section == QueueSection.Done ? new DoneStripViewModel() : null;

    /// <summary>A file comes: it shows at <see cref="Settle"/>, so that any number of them cost one arrangement.</summary>
    public void Enter(ItemViewModel row)
    {
        _rows.Add(row);
        _unsettled = true;
    }

    /// <summary>A file goes: it leaves at <see cref="Settle"/>.</summary>
    public void Leave(ItemViewModel row)
    {
        _leaving.Add(row);
        _unsettled = true;
    }

    /// <summary>The files that came and went since last time, shown.</summary>
    public void Settle()
    {
        if (!_unsettled)
            return;
        _unsettled = false;
        _rows.RemoveAll(_leaving.Contains);
        _leaving.Clear();
        Arrange();
    }

    /// <summary>Show or hide the files under the Done section's strip.</summary>
    public void ToggleFiles()
    {
        if (Strip is not { } strip)
            return;
        strip.ShowingFiles = !strip.ShowingFiles;
        Arrange();
    }

    /// <summary>The figures on the heads and the strip, as the files now are.</summary>
    /// <param name="byAlbum">every album's files in the queue, as an album's count of files isn't only those in this section</param>
    /// <param name="place">where the files are, in words: "In Downloads\Ferry"</param>
    /// <param name="canOpen">the files are on this PC, for their folder to be opened</param>
    /// <param name="width">how much room the rows have</param>
    public void RefreshSummaries(ILookup<string, ItemViewModel> byAlbum, string place, bool canOpen, RowWidth width)
    {
        foreach (var album in _albums.Values)
            album.Refresh(byAlbum[album.Name], width);
        if (Strip is { } strip)
        {
            strip.Refresh(_stripStale ? _rows : null, place, canOpen);
            _stripStale = false;
        }
    }

    protected override void ClearItems()
    {
        _rows.Clear();
        _leaving.Clear();
        _albums.Clear();
        _unsettled = false;
        base.ClearItems();
    }

    /// <summary>The list as it should be now, reached with as few changes as can be, so rows keep their place and selection.</summary>
    private void Arrange()
    {
        _stripStale = true;
        this.MatchTo(Wanted());
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(CountText)));
    }

    private List<object> Wanted()
    {
        var rows = _rows.Where(row => Filter.Length == 0 || row.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Strip is { } strip)
        {
            if (rows.Count == 0)
                return [];
            Together(rows);
            return strip.ShowingFiles || Filter.Length > 0 ? [strip, .. rows] : [strip];
        }
        var byHead = Section is QueueSection.Now or QueueSection.Stopped;
        var ofAlbum = byHead ? rows.Where(row => row.HasAlbum).ToLookup(row => row.Album, StringComparer.Ordinal) : null;
        var wanted = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var run = new List<ItemViewModel>(); // the plain files in a row, which share a card
        foreach (var row in rows)
        {
            if (!byHead || !row.HasAlbum)
            {
                run.Add(row);
                wanted.Add(row);
            }
            else if (seen.Add(row.Album))
            {
                Together(run);
                run.Clear();
                var album = _albums.TryGetValue(row.Album, out var known) ? known : _albums[row.Album] = new AlbumGroupViewModel(row.Album, Section == QueueSection.Stopped);
                List<ItemViewModel> files = [.. ofAlbum![row.Album]];
                album.Rows = files;
                wanted.Add(album);
                foreach (var file in files)
                {
                    file.Place(ReferenceEquals(file, files[^1]) ? RowPlace.Bottom : RowPlace.Middle, inAlbum: true);
                    wanted.Add(file);
                }
            }
        }
        Together(run);
        foreach (var gone in _albums.Keys.Except(seen).ToList())
            _albums.Remove(gone);
        return wanted;
    }

    /// <summary>These files in one card, with lines between.</summary>
    private static void Together(List<ItemViewModel> rows)
    {
        for (var i = 0; i < rows.Count; i++)
            rows[i].Place(rows.Count == 1 ? RowPlace.Alone : i == 0 ? RowPlace.Top : i == rows.Count - 1 ? RowPlace.Bottom : RowPlace.Middle, inAlbum: false);
    }
}

/// <summary>Where a file's row sits in its card.</summary>
public enum RowPlace
{
    Alone,
    Top,
    Middle,
    Bottom,
}

/// <summary>How much room a file's row has: all its columns, no column for the bar, which goes under its words, or no figures either.</summary>
public enum RowWidth
{
    Full,
    NoBarColumn,
    Compact,
}

/// <summary>What sums up a section: its figures change with every tick of a download, so each is only announced when it did.</summary>
public abstract partial class SummaryViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

/// <summary>The head of an album's files in Now or Paused: the album's cover, how far along it is, and what it is doing.</summary>
public sealed partial class AlbumGroupViewModel(string name, bool isStopped) : SummaryViewModel
{
    public string Name { get; } = name;
    public string Initial { get; } = AlbumCovers.Initial(name);
    public Brush Cover { get; } = AlbumCovers.Placeholder(name);

    /// <summary>Its files here are paused, so the head offers to carry on rather than to pause.</summary>
    public bool IsStopped { get; } = isStopped;
    public string ActionText => IsStopped ? "Resume the album" : "Pause the album";
    public string ActionGlyph => IsStopped ? "\uE768" : "\uE769";

    /// <summary>Its files in this section.</summary>
    public IReadOnlyList<ItemViewModel> Rows { get; set; } = [];

    public string Meta { get; private set => Set(ref field, value); } = "";
    public double Progress { get; private set => Set(ref field, value); }
    public string Speed { get; private set => Set(ref field, value); } = "";
    public string Size { get; private set => Set(ref field, value); } = "";

    /// <summary>As its files have it, so the ends of the bars line up.</summary>
    public GridLength FigureWidth { get; private set => Set(ref field, value); } = ItemViewModel.FiguresWidth;
    public GridLength ProgressWidth { get; private set => Set(ref field, value); } = ItemViewModel.BarWidth;
    public bool ShowFigures { get; private set => Set(ref field, value); } = true;
    public int ProgressColumn { get; private set => Set(ref field, value); } = 2;
    public int ProgressRow { get; private set => Set(ref field, value); }
    public Thickness ProgressMargin { get; private set => Set(ref field, value); }
    public string Percent { get; private set => Set(ref field, value); } = "0%";
    public bool ShowPercent { get; private set => Set(ref field, value); } = true;

    public override string ToString() => string.Join(", ", new[] { Name, Meta, Speed }.Where(s => s.Length > 0));

    /// <param name="rows">its files in the queue, in whichever section</param>
    public void Refresh(IEnumerable<ItemViewModel> rows, RowWidth width)
    {
        var full = width == RowWidth.Full;
        FigureWidth = width == RowWidth.Compact ? new GridLength(0) : ItemViewModel.FiguresWidth;
        ShowFigures = width != RowWidth.Compact;
        ProgressWidth = full ? ItemViewModel.BarWidth : new GridLength(0);
        (ProgressColumn, ProgressRow, ShowPercent) = full ? (2, 0, true) : (1, 1, false);
        ProgressMargin = full ? new Thickness(0) : new Thickness(0, 6, 0, 0);
        var (done, total, got, speed, count, source) = (0, 0L, 0L, 0.0, 0, "");
        foreach (var row in rows)
        {
            var item = row.Item;
            source = count++ == 0 ? row.Source : source;
            done += item.State is QueueState.Done or QueueState.Skipped ? 1 : 0;
            total += item.Total ?? 0;
            got += item.Done;
            speed += double.IsFinite(item.BytesPerSecond) ? item.BytesPerSecond : 0;
        }
        Meta = $"{(source.Length > 0 ? $"{source} · " : "")}{done} of {count} {(count == 1 ? "file" : "files")}";
        Progress = total > 0 ? Math.Min(100, got * 100.0 / total) : 0;
        Percent = $"{Progress:0}%";
        Speed = speed > 0 ? Units.Speed(speed) : "";
        Size = total > 0 ? $"{Units.Size(got)} of {Units.Size(total)}" : "";
    }
}

/// <summary>The Done section at a glance: how much, where, and the way to the files.</summary>
public sealed partial class DoneStripViewModel : SummaryViewModel
{
    private string _files = "";

    public string Summary { get; private set => Set(ref field, value); } = "";
    public string Detail { get; private set => Set(ref field, value); } = "";

    /// <summary>The files are on this PC, for their folder to be opened.</summary>
    public bool CanOpenFolder { get; private set => Set(ref field, value); } = true;

    public bool ShowingFiles
    {
        get;
        set
        {
            field = value;
            ToggleText = value ? "Hide files" : "Show files";
        }
    }

    public string ToggleText { get; private set => Set(ref field, value); } = "Show files";

    public override string ToString() => $"{Summary}, {Detail}";

    /// <param name="rows">the section's files, when they changed since last time</param>
    /// <param name="place">where they are, in words</param>
    /// <param name="canOpen">they are on this PC, for their folder to be opened</param>
    public void Refresh(IReadOnlyList<ItemViewModel>? rows, string place, bool canOpen)
    {
        if (rows is not null)
        {
            var albums = rows.Where(row => row.HasAlbum).Select(row => row.Album).Distinct(StringComparer.Ordinal).Count();
            var single = rows.Count(row => !row.HasAlbum);
            Summary = $"{rows.Count} file{(rows.Count == 1 ? "" : "s")}, {Units.Size(rows.Sum(row => row.Item.Total ?? row.Item.Done))}";
            var parts = new List<string>();
            if (albums > 0)
                parts.Add($"{albums} album folder{(albums == 1 ? "" : "s")}");
            if (single > 0)
                parts.Add($"{single} single file{(single == 1 ? "" : "s")}");
            _files = string.Join(" and ", parts);
        }
        Detail = $"{place}, {_files}";
        CanOpenFolder = canOpen;
    }
}

/// <summary>Which look an entry of the Downloads list has: a file, an album's head, or the Done strip.</summary>
public sealed partial class QueueEntryTemplates : DataTemplateSelector
{
    public DataTemplate? File { get; set; }
    public DataTemplate? Album { get; set; }
    public DataTemplate? Strip { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        AlbumGroupViewModel => Album,
        DoneStripViewModel => Strip,
        _ => File,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
