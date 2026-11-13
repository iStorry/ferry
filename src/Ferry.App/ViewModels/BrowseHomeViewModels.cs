using System.ComponentModel;
using System.Globalization;
using Ferry.App.Services;
using Ferry.Core.Browse;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ferry.App.ViewModels;

/// <summary>A watched album on the Browser page: how many files are new, and when it was last read.</summary>
public sealed class WatchedAlbumViewModel(WatchedAlbum album)
{
    public WatchedAlbum Album { get; } = album;

    public string Title => Album.Title;

    public string Initial { get; } = AlbumCovers.Initial(album.Title);

    public Brush Placeholder { get; } = AlbumCovers.Placeholder(album.Slug);

    public string NewText { get; } = album.NewFiles == 1 ? "1 new file" : $"{album.NewFiles} new files";

    public Visibility HasNew { get; } = album.NewFiles > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Why the last check failed, else when it was.</summary>
    public string Status { get; } = album switch
    {
        { Problem: { } problem } => $"Couldn't read it: {problem}",
        { CheckedAt: { } at, NewFiles: 0 } => $"Nothing new, checked {When(at)}",
        { CheckedAt: { } at } => $"Checked {When(at)}",
        _ => "Not checked yet",
    };

    /// <summary>The status in coral: it couldn't be read.</summary>
    public Visibility ProblemTag { get; } = album.Problem is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The status as it is: nothing is wrong, and no new files are there to say instead.</summary>
    public Visibility CheckedTag { get; } = album is { Problem: null, NewFiles: 0 } ? Visibility.Visible : Visibility.Collapsed;

    public override string ToString() => Album.NewFiles > 0 ? $"{Title}, {NewText}, {Status}" : $"{Title}, {Status}";

    /// <summary>The time for today, with the date for any other day.</summary>
    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.ToString(local.Date == TimeProvider.System.GetLocalNow().Date ? "t" : "g", CultureInfo.CurrentCulture);
    }
}

/// <summary>
/// An album on the site's list: its name, its files and size (the list's count until its own page is read), and
/// its picture once that is read; until then, or without one, a colour of its own with its initial.
/// </summary>
public sealed class AlbumCardViewModel(AlbumCard album) : INotifyPropertyChanged
{
    public AlbumCard Album { get; } = album;

    public string Title => Album.Title;

    /// <summary>The first letter or digit of the name, on the card until its picture shows.</summary>
    public string Initial { get; } = AlbumCovers.Initial(album.Title);

    /// <summary>The card's colour, the same for an album each time.</summary>
    public Brush Placeholder { get; } = AlbumCovers.Placeholder(album.Link.Slug);

    /// <summary>"663 files"; the list's count until the album's page is read.</summary>
    public string FilesText { get; private set; } = Count(album.Files);

    /// <summary>"2.13 GB", once the album's page says it.</summary>
    public string SizeText { get; private set; } = "";

    /// <summary>"663 files · 2.13 GB", under the card's name; the size once it is known.</summary>
    public string Facts => SizeText.Length > 0 ? $"{FilesText} · {SizeText}" : FilesText;

    /// <summary>The pointer is on the card: its outline and its action show.</summary>
    public bool IsPointerOver { get; private set; }

    public double HoverOpacity => IsPointerOver ? 1 : 0;

    /// <summary>"Adding…", then "663 added": the album going to Downloads from its card.</summary>
    public string DownloadNote { get; private set; } = "";

    public Visibility HasDownloadNote => DownloadNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The album is being read to go to Downloads: its card doesn't send it twice meanwhile.</summary>
    public bool Downloading { get; private set; }

    /// <summary>The album went to Downloads from its card: its button shows a tick.</summary>
    public bool Added { get; private set; }

    public double DoneOpacity => Added ? 1 : 0;

    public double ArrowOpacity => 1 - DoneOpacity;

    /// <param name="added">every file of it is in Downloads now</param>
    public void ShowDownload(string note, bool downloading, bool added = false)
    {
        (DownloadNote, Downloading, Added) = (note, downloading, added || Added);
        foreach (var name in new[] { nameof(DownloadNote), nameof(HasDownloadNote), nameof(DoneOpacity), nameof(ArrowOpacity) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void SetPointerOver(bool over)
    {
        if (over == IsPointerOver)
            return;
        IsPointerOver = over;
        foreach (var name in new[] { nameof(IsPointerOver), nameof(HoverOpacity) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>The album's own page has been read, or tried.</summary>
    public bool DetailsRead { get; private set; }

    /// <summary>The album's size, once its own page says it.</summary>
    public long? Size { get; private set; }

    /// <summary>The picture has been asked for (it is read once, when the card is first shown).</summary>
    public bool PictureAsked { get; set; }

    /// <summary>Empty until the page sets the picture's bytes in it (they come through the reader, not from here); null without a picture.</summary>
    public BitmapImage? Thumbnail { get; } = album.Thumbnail is null ? null : new BitmapImage { DecodePixelWidth = AlbumCovers.ThumbnailPixels };

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What the album's page said, or null when it couldn't be read (the list's count stays).</summary>
    public void Read(AlbumDetails? details)
    {
        DetailsRead = true;
        Size = details?.Size;
        FilesText = Count(details?.Files ?? Album.Files);
        SizeText = Size is { } bytes ? Units.Size(bytes) : "";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Facts)));
    }

    public override string ToString() => Size is null ? $"{Title}, {FilesText}" : $"{Title}, {FilesText}, {SizeText}";

    private static string Count(int? files) => files switch
    {
        null => "Album",
        1 => "1 file",
        var n => $"{n:N0} files",
    };
}
