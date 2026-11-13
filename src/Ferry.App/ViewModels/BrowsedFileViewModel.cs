using System.ComponentModel;
using System.Globalization;
using Ferry.App.Services;
using Ferry.Core.Browse;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ferry.App.ViewModels;

/// <summary>One card of the Browser page: a file on the page that was opened, put into words.</summary>
/// <param name="isNew">added to a watched album since the user last looked</param>
/// <param name="inDownloads">already in the Downloads queue</param>
/// <param name="downloaded">downloaded here before (<see cref="Ferry.Core.Queue.DownloadedFiles"/>), in the queue or not</param>
public sealed class BrowsedFileViewModel(BrowsedFile file, bool isNew, bool inDownloads, bool downloaded) : INotifyPropertyChanged
{
    public BrowsedFile File { get; } = file;

    public string Name => File.Name;

    public string Glyph { get; } = Glyphs.ForFile(file.Name);

    /// <summary>"1.2 GB · 14/09/2026": the size and upload date, as far as the page says.</summary>
    public string Detail { get; } = string.Join(" · ", new[]
    {
        file.Size is { } size ? Units.Size(size) : "Size not shown",
        file.Uploaded?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
    }.OfType<string>());

    /// <summary>Empty until the page sets the picture's bytes in it, as the card comes into view; null without a picture.</summary>
    public BitmapImage? Thumbnail { get; } = file.Thumbnail is null ? null : new BitmapImage { DecodePixelWidth = AlbumCovers.ThumbnailPixels };

    /// <summary>The picture has been asked for.</summary>
    public bool PictureAsked { get; set; }

    public Visibility NewTag { get; } = isNew ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The file is in the Downloads queue; it changes as files are added there.</summary>
    public bool InDownloads { get; private set; } = inDownloads;

    /// <summary>The file was downloaded here before; it changes as files finish.</summary>
    public bool Downloaded { get; private set; } = downloaded;

    /// <summary>"In Downloads" while it is queued and not yet downloaded; "Downloaded" once it is.</summary>
    public Visibility InDownloadsTag => InDownloads && !Downloaded ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DownloadedTag => Downloaded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The download button: its arrow, or once the file is queued or had, a tick.</summary>
    public double DoneOpacity => InDownloads || Downloaded ? 1 : 0;

    public double ArrowOpacity => 1 - DoneOpacity;

    /// <summary>The pointer is on the tile: its download button shows.</summary>
    public bool IsPointerOver { get; private set; }

    public double HoverOpacity => IsPointerOver ? 1 : 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetPointerOver(bool over)
    {
        if (over == IsPointerOver)
            return;
        IsPointerOver = over;
        foreach (var name in new[] { nameof(IsPointerOver), nameof(HoverOpacity) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void SetHad(bool inDownloads, bool downloaded)
    {
        if (inDownloads == InDownloads && downloaded == Downloaded)
            return;
        (InDownloads, Downloaded) = (inDownloads, downloaded);
        foreach (var name in new[] { nameof(InDownloadsTag), nameof(DownloadedTag), nameof(DoneOpacity), nameof(ArrowOpacity) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>What a screen reader says for the card.</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Name, Detail, isNew ? "new" : null, Downloaded ? "downloaded" : InDownloads ? "in Downloads" : null }.OfType<string>());
}
