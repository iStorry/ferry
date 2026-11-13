using System.ComponentModel;
using Ferry.App.Services;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Queue;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Ferry.App.ViewModels;

/// <summary>One row of the Downloads page: a <see cref="QueueItem"/> put into words, as docs/mocks/downloads.html shows it.</summary>
public sealed partial class ItemViewModel(QueueItem item) : INotifyPropertyChanged
{
    // how far a row's card goes towards the text, pointed at and selected
    private const double PointedAtLift = 0.05;
    private const double SelectedLift = 0.1;

    // the wash behind a kind's glyph: its own colour, faint
    private const byte KindWashAlpha = 0x21;

    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    // what changes with every tick of a download; the rest changes with its state, or when it is said again
    private static readonly PropertyChangedEventArgs[] ProgressChanged = Changes(
        nameof(Progress), nameof(Percent), nameof(ShowPercent), nameof(FigureTop), nameof(ShowFigureTop), nameof(FigureBottom), nameof(ShowFigures),
        nameof(Detail), nameof(Meta), nameof(ServerText), nameof(ServerValue), nameof(PcText), nameof(PcValue));

    private static readonly PropertyChangedEventArgs[] PlaceChanged = Changes(nameof(RowMargin), nameof(RowBorder), nameof(RowCorners), nameof(Meta));
    private static readonly PropertyChangedEventArgs[] MarkChanged = Changes(nameof(RowBackground), nameof(ActionsOpacity));

    // shared by every row: a tick of a download makes no brushes
    private static readonly Dictionary<(FileKind Kind, bool Dark, bool HighContrast), (Brush Ink, Brush Wash)> KindInks = [];
    private static readonly Dictionary<(bool Dark, bool Selected, bool HighContrast), Brush> Lifts = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public QueueItem Item { get; private set; } = item;

    public Guid Id => Item.Id;

    public string Name { get; private set; } = item.DisplayName;

    /// <summary>The album it is downloaded for (its folder in the download folder), or "" for none.</summary>
    public string Album { get; private set; } = "";
    public bool HasAlbum => Album.Length > 0;

    /// <summary>Where on the Downloads page it shows.</summary>
    public QueueSection Section => QueueSections.Of(Item);

    public string KindGlyph { get; private set; } = Glyphs.OtherFile;

    /// <summary>The kind's own colour, on a wash of it (docs/mocks/ferry.css, .kind).</summary>
    public Brush? KindBrush { get; private set; }
    public Brush? KindBackground { get; private set; }

    /// <summary>A file further down the line takes less room.</summary>
    public double TileSize => Section == QueueSection.UpNext ? 28 : 36;
    public double TileGlyphSize => Section == QueueSection.UpNext ? 15 : 18;
    public CornerRadius TileCorners => new(Section == QueueSection.UpNext ? 8 : 10);

    /// <summary>On the row's right: what it is doing now (its speed), and how that goes (the time left, or its state).</summary>
    public string FigureTop { get; private set; } = "";
    public string FigureBottom { get; private set; } = "";
    public bool ShowFigureTop => FigureTop.Length > 0;

    /// <summary>The same on every row, so the ends of the bars line up.</summary>
    public static GridLength FiguresWidth { get; } = new(112);

    /// <summary>The column the bar has when there is room for it.</summary>
    public static GridLength BarWidth { get; } = new(168);

    /// <summary>A problem's words get the row's width; there is nothing of it to measure.</summary>
    public GridLength FigureWidth => Width == RowWidth.Compact || Item.State == QueueState.Failed ? new GridLength(0) : FiguresWidth;
    public GridLength ProgressWidth => Width == RowWidth.Full && Item.State != QueueState.Failed ? BarWidth : new GridLength(0);
    public bool ShowFigures => Width != RowWidth.Compact && (FigureTop.Length > 0 || FigureBottom.Length > 0);

    /// <summary>Where the bar goes: its own column, or under the file's words when there is no room.</summary>
    public int ProgressColumn => Width == RowWidth.Full ? 2 : 1;
    public int ProgressRow => Width == RowWidth.Full ? 0 : 1;
    public Thickness ProgressMargin => Width == RowWidth.Full ? new Thickness(0) : new Thickness(0, 6, 0, 0);
    public string Percent => $"{Progress:0}%";
    public bool ShowPercent => Width == RowWidth.Full && ShowBar && !Indeterminate;

    /// <summary>Selected or pointed at, as the list's own marks can't show through a solid row.</summary>
    public Brush? RowBackground => Selected || Hovered ? Lifted() : ThemeBrushes.Get(ThemeBrushes.Card, _theme);
    public Brush? RowBorderBrush { get; private set; }

    public Thickness RowMargin { get; private set; } = new(0, 8, 0, 0);
    private RowPlace _place;
    private bool _inAlbum;
    private bool _placed;
    private ElementTheme _theme;
    public Thickness RowBorder { get; private set; } = new(1);
    public CornerRadius RowCorners { get; private set; } = new(12);

    /// <summary>Which plugin read it; "" until it is known. Inside an album's card its head says.</summary>
    public string Source { get; set; } = "";

    /// <summary>The line under its name.</summary>
    public string Meta => Source.Length == 0 || _inAlbum || Item.State == QueueState.Failed
        ? Detail
        : Detail.Length > 0 ? $"{Source} · {Detail}" : Source;

    /// <summary>Selected in the list: drawn by the row.</summary>
    public bool Selected
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Raise(MarkChanged);
        }
    }

    /// <summary>The pointer is over it.</summary>
    public bool Hovered
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Raise(MarkChanged);
        }
    }

    /// <summary>What can be done shows when it is wanted: on hover, when selected, in a narrow window, and always for what needs the user.</summary>
    public double ActionsOpacity => Hovered || Selected || Width == RowWidth.Compact || Item.State == QueueState.Failed ? 1 : 0;

    public string StateText { get; private set; } = "";
    public Brush? StateBrush { get; private set; }

    public bool ShowBar { get; private set; }
    public double Progress { get; private set; }
    public bool Indeterminate { get; private set; }
    public bool Paused { get; private set; }
    public bool Failed { get; private set; }
    public Brush? BarBrush { get; private set; }

    /// <summary>Server mode: the file's two steps, the server's download and the transfer here.</summary>
    public bool ShowSteps { get; private set; }
    public string ServerText { get; private set; } = "";
    public double ServerValue { get; private set; }
    public Brush? ServerBrush { get; private set; }
    public string PcText { get; private set; } = "";
    public double PcValue { get; private set; }

    public string Detail { get; private set; } = "";
    public Brush? DetailBrush { get; private set; }

    /// <summary>Lines the detail may take: all of an error (0, no limit), two otherwise.</summary>
    public int DetailLines => Item.State == QueueState.Failed ? 0 : 1;

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => string.Join(", ", new[] { Name, StateText, Detail }.Where(s => s.Length > 0).Distinct());

    public bool CanPause { get; private set; }
    public bool CanResume { get; private set; }
    public bool CanCancel { get; private set; }
    public bool CanRetry { get; private set; }
    public bool CanOpenFolder { get; private set; }
    public bool CanRename { get; private set; }

    /// <summary>How much room the row has.</summary>
    public RowWidth Width { get; private set; }

    public bool ShowCancel => CanCancel && !(CanPause || CanResume);
    public bool ShowRetry => CanRetry;
    public bool ShowOpenFolder => CanOpenFolder && !CanRetry;

    /// <summary>What the row's looks follow, apart from the figures of a download going on.</summary>
    private (QueueState State, string Name, string Glyph, string Album, bool ShowBar, bool CanOpenFolder) Shape =>
        (Item.State, Name, KindGlyph, Album, ShowBar, CanOpenFolder);

    /// <summary>Its place in the card it is in, as its section arranges them: rows in one card have lines between.</summary>
    public void Place(RowPlace place, bool inAlbum)
    {
        if (place == _place && inAlbum == _inAlbum && _placed)
            return;
        (_place, _inAlbum, _placed) = (place, inAlbum, true);
        ApplyPlace();
        Raise(PlaceChanged);
    }

    private void ApplyPlace()
    {
        var starts = _place is RowPlace.Alone or RowPlace.Top;
        RowMargin = new Thickness(0, starts ? 8 : 0, 0, 0);
        RowBorder = starts ? new Thickness(1) : new Thickness(1, 0, 1, 1);
        RowCorners = _place switch
        {
            RowPlace.Alone => new CornerRadius(12),
            RowPlace.Top => new CornerRadius(12, 12, 0, 0),
            RowPlace.Bottom => new CornerRadius(0, 0, 12, 12),
            _ => new CornerRadius(0),
        };
    }

    /// <summary>The system's high contrast colours changed: the brushes made from them are out of date.</summary>
    public static void ForgetBrushes()
    {
        KindInks.Clear();
        Lifts.Clear();
    }

    /// <summary>The card a little towards the text: pointed at, more so selected.</summary>
    private Brush Lifted()
    {
        var key = (_theme == ElementTheme.Dark, Selected, ThemeBrushes.HighContrast);
        if (Lifts.TryGetValue(key, out var known))
            return known;
        var (card, ink) = (((SolidColorBrush)ThemeBrushes.Get(ThemeBrushes.Card, _theme)).Color, ((SolidColorBrush)ThemeBrushes.Get(ThemeBrushes.Primary, _theme)).Color);
        var amount = Selected ? SelectedLift : PointedAtLift;
        byte Mix(byte under, byte over) => (byte)(under + (over - under) * amount);
        return Lifts[key] = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, Mix(card.R, ink.R), Mix(card.G, ink.G), Mix(card.B, ink.B)));
    }

    /// <summary>The kind's colour for the theme (docs/mocks/ferry.css), or null for a kind that has none of its own.</summary>
    private static Windows.UI.Color? KindColor(FileKind kind, bool dark) => kind switch
    {
        FileKind.Video => dark ? Windows.UI.Color.FromArgb(0xFF, 0xA7, 0x95, 0xFB) : Windows.UI.Color.FromArgb(0xFF, 0x6D, 0x4F, 0xD8),
        FileKind.Image => dark ? Windows.UI.Color.FromArgb(0xFF, 0x5C, 0xCB, 0xA8) : Windows.UI.Color.FromArgb(0xFF, 0x12, 0x80, 0x5F),
        FileKind.Archive => dark ? Windows.UI.Color.FromArgb(0xFF, 0xE8, 0xAB, 0x5E) : Windows.UI.Color.FromArgb(0xFF, 0xB7, 0x6A, 0x0C),
        FileKind.Audio => dark ? Windows.UI.Color.FromArgb(0xFF, 0xF1, 0x90, 0xBB) : Windows.UI.Color.FromArgb(0xFF, 0xC2, 0x3F, 0x7D),
        FileKind.Document => dark ? Windows.UI.Color.FromArgb(0xFF, 0x80, 0xB5, 0xF5) : Windows.UI.Color.FromArgb(0xFF, 0x2A, 0x6F, 0xC0),
        _ => null,
    };

    /// <summary>The kind's colour and the wash of it behind its glyph; in ink for a kind that has none.</summary>
    private static (Brush Ink, Brush Wash) KindInk(FileKind kind, ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        var key = (kind, dark, ThemeBrushes.HighContrast);
        if (KindInks.TryGetValue(key, out var known))
            return known;
        return KindInks[key] = KindColor(kind, dark) is { } color
            ? (new SolidColorBrush(color), new SolidColorBrush(Windows.UI.Color.FromArgb(KindWashAlpha, color.R, color.G, color.B)))
            : (ThemeBrushes.Get(ThemeBrushes.Secondary, theme), ThemeBrushes.Get(ThemeBrushes.Subtle, theme));
    }

    /// <summary>Put <paramref name="item"/> into words; ignored unless it is newer than what is shown.</summary>
    public void Update(QueueItem item, RowContext context)
    {
        if (item.Revision <= Item.Revision)
            return;
        var was = Shape;
        Item = item;
        Describe(context);
        if (Shape == was)
            Raise(ProgressChanged);
        else
            PropertyChanged?.Invoke(this, AllChanged);
    }

    /// <summary>Say the same item again: what it depends on besides itself changed.</summary>
    public void Refresh(RowContext context)
    {
        Describe(context);
        PropertyChanged?.Invoke(this, AllChanged);
    }

    private static PropertyChangedEventArgs[] Changes(params string[] names) => [.. names.Select(name => new PropertyChangedEventArgs(name))];

    private void Raise(PropertyChangedEventArgs[] changes)
    {
        foreach (var change in changes)
            PropertyChanged?.Invoke(this, change);
    }

    private void Describe(RowContext context)
    {
        var item = Item;
        (Width, _theme) = (context.Width, context.Theme);
        Brush Tone(string key) => ThemeBrushes.Get(key, context.Theme);
        var (server, machine) = (context.Where == DownloadPlace.Server, context.WhereDisplay);
        var there = server ? "the server" : machine;
        Name = item.DisplayName;
        var kind = FileKinds.Of(item.Path ?? item.Name ?? item.Link.Label);
        KindGlyph = Glyphs.For(kind);
        (KindBrush, KindBackground) = KindInk(kind, context.Theme);
        Album = QueueSections.AlbumOf(item.Folder, context.AlbumRoot) ?? "";
        (ShowBar, Indeterminate, Paused, Failed, ShowSteps) = (false, false, false, false, false);
        (FigureTop, FigureBottom) = ("", "");
        RowBorderBrush = Tone(ThemeBrushes.CardStroke);
        // in Now the album's head says it; further down, the line under the name does
        var inAlbum = HasAlbum && QueueSections.Of(item) != QueueSection.Now ? $"{Album} · " : "";
        Progress = PercentOf(item);
        BarBrush = context.Accent;
        StateBrush = Tone(ThemeBrushes.Secondary);
        DetailBrush = Tone(ThemeBrushes.Secondary);
        var size = Sizes(item);
        var retries = item.Retries switch { 0 => "", 1 => " · 1 retry", var n => $" · {n} retries" };

        CanPause = item.State is QueueState.Waiting or QueueState.Resolving or QueueState.Downloading or QueueState.OnServer or QueueState.Transferring;
        CanResume = item.State == QueueState.Paused;
        CanCancel = item.State is QueueState.Waiting or QueueState.Resolving or QueueState.Downloading or QueueState.Paused
                                  or QueueState.OnServer or QueueState.Transferring;
        CanRetry = item.State is QueueState.Failed or QueueState.Cancelled;
        CanOpenFolder = context.Where != DownloadPlace.RemoteQueue && item.State is QueueState.Done or QueueState.Skipped && item.Path is not null;
        CanRename = item.State == QueueState.Waiting && item.Path is null;

        switch (item.State)
        {
            case QueueState.Waiting:
                StateText = "Waiting";
                Detail = inAlbum + (context.Where != DownloadPlace.ThisPc ? $"Waiting on {there}"
                    : context.Schedule is { } schedule && !schedule.IsOpen(TimeOnly.FromDateTime(DateTime.Now))
                    ? $"Starts at {schedule.Start:HH:mm}, on the schedule"
                    : item.Total is { } known ? Units.Size(known) : "Size not known yet");
                break;
            case QueueState.Resolving:
                StateText = "Reading the link";
                (ShowBar, Indeterminate) = (true, true);
                Detail = context.Where == DownloadPlace.ThisPc ? StateText : $"{(server ? "The server" : machine)} is reading the link";
                break;
            case QueueState.Downloading when server:
                StateText = "On the server";
                Steps(context, ($"Server {Units.Percent(item.Done, item.Total).Trim()}", Progress, false), ("Here next", 0, false));
                (FigureTop, FigureBottom) = (item.BytesPerSecond > 0 ? Units.Speed(item.BytesPerSecond) : "", "on the server");
                break;
            case QueueState.OnServer:
                StateText = "Waiting to come here";
                Steps(context, ("Server", 100, true), ("Here next", 0, false));
                break;
            case QueueState.Transferring:
                StateText = "Coming here";
                Steps(context, ("Server", 100, true), ($"Here {Units.Percent(item.Done, item.Total).Trim()}", Progress, false));
                (FigureTop, FigureBottom) = (item.BytesPerSecond > 0 ? Units.Speed(item.BytesPerSecond) : "", item.TimeLeft is { } coming ? $"{Units.Time(coming)} left" : "");
                break;
            case QueueState.Downloading:
                StateText = "Downloading";
                ShowBar = true;
                Detail = $"{size}{(item.Connections > 0 ? $" · {item.Connections} connection{(item.Connections == 1 ? "" : "s")}" : "")}{retries}";
                (FigureTop, FigureBottom) = (item.BytesPerSecond > 0 ? Units.Speed(item.BytesPerSecond) : "", item.TimeLeft is { } remaining ? $"{Units.Time(remaining)} left" : "");
                break;
            case QueueState.Paused:
                StateText = "Paused";
                StateBrush = Tone(ThemeBrushes.Caution);
                (ShowBar, Paused) = (item.Done > 0, true);
                FigureBottom = StateText;
                Detail = server && OnThisPc(item) ? $"{size} here · the rest comes here when resumed"
                         : server && item.Total is { } whole && item.Done >= whole ? "Downloaded on the server · comes here when resumed"
                         : item.Done <= 0 ? "Resume puts it back in line"
                         : context.Where == DownloadPlace.ThisPc ? $"{size} kept · resumes where it stopped"
                         : $"{size} on {there} · resumes there";
                break;
            case QueueState.Cancelled:
                StateText = "Cancelled";
                FigureBottom = StateText;
                Detail = item.Done <= 0 ? "Retry puts it back in line"
                         : context.Where == DownloadPlace.ThisPc || (server && OnThisPc(item)) ? $"{Units.Size(item.Done)} kept · Retry carries on from there"
                         : $"{Units.Size(item.Done)} kept on {there} · Retry carries on from there";
                break;
            case QueueState.Failed:
                StateText = "Failed";
                StateBrush = Tone(ThemeBrushes.Critical);
                DetailBrush = Tone(ThemeBrushes.Critical);
                Detail = $"{FailedStep(item.FailedAt)}{item.Error}";
                break;
            case QueueState.Done:
                StateText = "Done";
                var finished = Units.Size(item.Total ?? item.Done);
                var hashed = item.Sha256 is not null ? " · SHA-256 checked" : "";
                Detail = context.Where switch
                {
                    DownloadPlace.Server => $"{finished} · from {machine}{hashed} · {FolderOf(item, context)}",
                    DownloadPlace.RemoteQueue => $"{finished} · on {machine}",
                    _ => $"{finished}{hashed} · {FolderOf(item, context)}",
                };
                break;
            case QueueState.Skipped:
                StateText = "Skipped";
                // the file was already here and nothing was fetched, so nothing about it was checked
                Detail = context.Where switch
                {
                    DownloadPlace.Server => $"{Units.Size(item.Total ?? item.Done)} · from {machine} · already in {FolderOf(item, context)}",
                    DownloadPlace.RemoteQueue => $"Already on {machine}",
                    _ => $"Already in {FolderOf(item, context)}",
                };
                break;
        }
        // nothing has been heard for a while: this is how it stood then, and a speed from then would be a false one
        if (context.Stale && CanPause)
        {
            (FigureTop, FigureBottom) = ("", "");
            (ShowBar, Indeterminate) = (ShowBar && !Indeterminate, false);
            Detail = Detail.Length > 0 ? $"{Detail} · last known" : "Last known";
        }
    }

    private void Steps(RowContext context, (string Text, double Value, bool Done) server, (string Text, double Value, bool Done) pc)
    {
        ShowSteps = true;
        Detail = Sizes(Item);
        (ServerText, ServerValue) = (server.Text, server.Value);
        // what is done is in ink; colour is for what is moving
        ServerBrush = server.Done ? ThemeBrushes.Get(ThemeBrushes.Tertiary, context.Theme) : context.Accent;
        (PcText, PcValue) = (pc.Text, pc.Value);
    }

    private static string FailedStep(Stage? stage) => stage switch
    {
        Stage.Link => "The link: ",
        Stage.Resolve => "Reading the link: ",
        Stage.Download => "Downloading: ",
        Stage.Server => "Reaching the server: ",
        Stage.Transfer => "Bringing it here: ",
        Stage.Verify => "Checking the file: ",
        _ => "",
    };

    private static string FolderOf(QueueItem item, RowContext context) =>
        Folders.Shorten(Path.GetDirectoryName(item.Path) is { Length: > 0 } folder ? folder : context.DownloadFolder);

    /// <summary>
    /// In server mode, whether the file's bytes so far are on this PC (a transfer) rather than on the server. The agents
    /// run on Linux, and their paths aren't fully qualified on Windows, where a drive's or a network share's is.
    /// </summary>
    internal static bool OnThisPc(QueueItem item) => item.Path is { } path && Path.IsPathFullyQualified(path);

    private static double PercentOf(QueueItem item) =>
        item.Total is > 0 ? Math.Min(100, item.Done * 100.0 / item.Total.Value) : 0;

    private static string Sizes(QueueItem item) =>
        item.Total is { } total ? $"{Units.Size(item.Done)} of {Units.Size(total)}" : Units.Size(item.Done);
}

/// <summary>What a row says and how it looks depends on, besides its file. One is made for all the rows, when any of this changes.</summary>
/// <param name="Where">where downloads run</param>
/// <param name="WhereDisplay">what to call that place</param>
/// <param name="Stale">the server hasn't been heard from for a while, so what a row says may be long out of date</param>
/// <param name="DownloadFolder">where files land on this PC</param>
/// <param name="AlbumRoot">the folder an album's folder is in, on the machine that downloads</param>
/// <param name="Schedule">the hours downloads here may run, or null when they always may</param>
/// <param name="Accent">the accent for the theme: changing it makes a new brush</param>
/// <param name="Width">how much room the rows have</param>
public sealed record RowContext(DownloadPlace Where, string WhereDisplay, bool Stale, string DownloadFolder, string AlbumRoot, DownloadSchedule? Schedule, Brush Accent,
                                ElementTheme Theme, RowWidth Width);
