using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Queue;
using Ferry.Core.Sites;
using Ferry.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Ferry.App.Services;

/// <summary>
/// Reads what a link leads to, where the download will happen (this PC, or the server in server mode), so that the site
/// hears from that machine alone: the file's name and size as its site gives them.
/// </summary>
/// <exception cref="StageException">it couldn't be read</exception>
public delegate Task<(string Name, long? Size)> LinkReader(FileLink link, RequestDetails details, CancellationToken cancel);

/// <summary>
/// What is about to be added, before it is (docs/mocks/add.html): each link's name and size as its server gives them, which plugin
/// reads it, where the files go, and for a file behind a login what to send with the requests. Reading the links goes on while the
/// dialog is open, where the download will happen (<see cref="LinkReader"/>).
/// </summary>
public sealed class AddLinksDialog : IDisposable
{
    private const int LinksAtOnce = 4;
    private const double ListHeight = 180;

    private readonly SiteCatalog _sites;
    private readonly IReadOnlyList<FileLink> _links;
    private readonly ElementTheme _theme;
    private readonly List<(FileLink Link, TextBlock Name, TextBlock Size, TextBlock Figure, ProgressRing Reading, Border Tile, FontIcon Glyph)> _rows = [];
    private readonly TextBox _name = new() { Header = "Save as" };
    private readonly TextBlock _folder = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _referer = new() { Header = "Referer", PlaceholderText = "The page the link came from" };
    private readonly TextBox _cookies = new() { Header = "Cookies", PlaceholderText = "name=value; name2=value2" };
    private readonly TextBox _headers = new() { Header = "More headers", PlaceholderText = "Name: value, one a line", AcceptsReturn = true, MinHeight = 72 };
    private readonly TextBox _checksum = new() { Header = "SHA-256 the finished file should have", PlaceholderText = "From the page you got the link from" };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock _free = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private CancellationTokenSource _reading = new();
    private RequestDetails _details = RequestDetails.None;
    private string? _sha256;
    private string _chosenFolder;
    private readonly LinkReader _read;
    private readonly bool _onServer;

    private AddLinksDialog(SiteCatalog sites, IReadOnlyList<FileLink> links, string folder, ElementTheme theme, LinkReader read, bool onServer)
    {
        (_sites, _links, _chosenFolder, _theme, _read, _onServer) = (sites, links, folder, theme, read, onServer);
    }

    /// <summary>Ask; the options to add the links with, or null when cancelled.</summary>
    /// <param name="sites">which plugin each link is read by, for the row</param>
    /// <param name="read">reads each link for its name and size, where the download will happen</param>
    /// <param name="onServer">a server downloads the files: it reads the links and gets the login, and the folder here isn't chosen</param>
    public static async Task<AddOptions?> ShowAsync(XamlRoot? root, SiteCatalog sites, IReadOnlyList<FileLink> links, string folder, ElementTheme theme,
                                                    LinkReader read, bool onServer)
    {
        using var dialog = new AddLinksDialog(sites, links, folder, theme, read, onServer);
        var content = dialog.Build();
        _ = dialog.ReadLinksAsync();
        var title = links.Count == 1 ? "Add this download?" : $"Add {links.Count} downloads?";
        var choice = await Dialogs.ChooseAsync(root, title, content, "Start", "Add paused", dialog.ReadDetails);
        await dialog._reading.CancelAsync();
        return choice == ContentDialogResult.None ? null : dialog.Options(folder, paused: choice == ContentDialogResult.Secondary);
    }

    public void Dispose() => _reading.Dispose();

    private AddOptions Options(string defaultFolder, bool paused) => new(
        _links.Count == 1 && _name.Text.Trim() is { Length: > 0 } name ? name : null,
        _chosenFolder == defaultFolder ? null : _chosenFolder,
        _details,
        paused,
        _sha256);

    private StackPanel Build()
    {
        var content = new StackPanel { Spacing = 12, MinWidth = 420 };
        content.Children.Add(BuildLinks());
        if (_links.Count == 1)
            content.Children.Add(_name);
        if (!_onServer)
            content.Children.Add(BuildFolder());
        content.Children.Add(BuildDetails());
        _problem.Foreground = ThemeBrushes.Get(ThemeBrushes.Critical, _theme);
        content.Children.Add(_problem);
        return content;
    }

    private Border BuildLinks()
    {
        var list = new StackPanel();
        foreach (var (link, at) in _links.Select((link, at) => (link, at)))
        {
            var glyph = new FontIcon { Glyph = "\uE71B", FontSize = 14, Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, _theme) };
            var tile = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center, Background = ThemeBrushes.Get(ThemeBrushes.Subtle, _theme), Child = glyph };
            var name = new TextBlock { Text = link.Label, TextTrimming = TextTrimming.CharacterEllipsis };
            var size = new TextBlock { Text = "Reading…", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionMutedStyle"] };
            ToolTipService.SetToolTip(name, link.Original.ToString());
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(name);
            words.Children.Add(size);
            Grid.SetColumn(words, 1);
            var figure = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, _theme) };
            var reading = new ProgressRing { Width = 14, Height = 14, MinWidth = 14, MinHeight = 14, VerticalAlignment = VerticalAlignment.Center, IsActive = true };
            var end = new Grid();
            end.Children.Add(figure);
            end.Children.Add(reading);
            Grid.SetColumn(end, 2);
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(tile);
            grid.Children.Add(words);
            grid.Children.Add(end);
            list.Children.Add(new Border
            {
                Padding = new Thickness(12, 7, 12, 7),
                BorderThickness = at == 0 ? new Thickness(0) : new Thickness(0, 1, 0, 0),
                BorderBrush = ThemeBrushes.Get(ThemeBrushes.CardStroke, _theme),
                Child = grid,
            });
            _rows.Add((link, name, size, figure, reading, tile, glyph));
        }
        return new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrushes.Get(ThemeBrushes.CardStroke, _theme),
            Child = new ScrollViewer { Content = list, MaxHeight = ListHeight },
        };
    }

    private Border BuildFolder()
    {
        _folder.Text = _chosenFolder;
        ToolTipService.SetToolTip(_folder, _chosenFolder);
        ShowFree();
        var icon = new FontIcon { Glyph = "\uE838", FontSize = 14, Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, _theme) };
        _free.Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, _theme);
        Grid.SetColumn(_folder, 1);
        Grid.SetColumn(_free, 2);
        var change = new Button { Content = "Change…", Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        change.Click += async (_, _) => await PickFolderAsync();
        Grid.SetColumn(change, 3);
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        row.Children.Add(_folder);
        row.Children.Add(_free);
        row.Children.Add(change);
        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 6, 6, 6),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrushes.Get(ThemeBrushes.CardStroke, _theme),
            Background = ThemeBrushes.Get(ThemeBrushes.Sunk, _theme),
            Child = row,
        };
    }

    /// <summary>The room left where they would go, if the drive says.</summary>
    private void ShowFree()
    {
        try
        {
            _free.Text = $"{Units.Size(new DriveInfo(_chosenFolder).AvailableFreeSpace)} free";
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _free.Text = ""; // a drive that's gone, or a share that won't say: choosing it still says why when it is used
        }
    }

    private Expander BuildDetails()
    {
        var fields = new StackPanel { Spacing = 8 };
        if (_links.Count == 1)
            fields.Children.Add(_checksum);
        foreach (var box in new[] { _referer, _cookies, _headers })
        {
            box.LostFocus += (_, _) => OnDetailsEdited();
            fields.Children.Add(box);
        }
        fields.Children.Add(new TextBlock
        {
            Text = _onServer
                ? "Cookies and headers are for a file behind a login. They go to your server with the link, stay in its memory until the file is done, and go only to the link's site."
                : "Cookies and headers are for a file behind a login. They stay encrypted on this PC until the file is done, and go only to the link's site.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionMutedStyle"],
        });
        return new Expander
        {
            Header = _links.Count == 1 ? "Checksum and login" : "Login",
            Content = fields,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private async Task PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window!.Handle);
        if (await picker.PickSingleFolderAsync() is not { } folder)
            return;
        _chosenFolder = folder.Path;
        _folder.Text = folder.Path;
        ToolTipService.SetToolTip(_folder, folder.Path);
        ShowFree();
    }

    /// <summary>What was typed, as details; false, and it says why, while it can't be used.</summary>
    private bool ReadDetails()
    {
        var lines = _headers.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!RequestDetails.TryCreate(_referer.Text, _cookies.Text, lines, out var details, out var reason))
        {
            (_problem.Text, _problem.Visibility) = (reason, Visibility.Visible);
            return false;
        }
        if (!FileChecksum.TryRead(_checksum.Text, out var sha256))
        {
            (_problem.Text, _problem.Visibility) = ("That is not a SHA-256, which is 64 letters and digits.", Visibility.Visible);
            return false;
        }
        _problem.Visibility = Visibility.Collapsed;
        (_details, _sha256) = (details, sha256);
        return true;
    }

    /// <summary>A login may be what a link needed: read them again with what was typed, if it changed anything.</summary>
    private void OnDetailsEdited()
    {
        var before = _details;
        if (ReadDetails() && _details != before)
            _ = ReadLinksAsync();
    }

    /// <summary>Each link's name and size, as they come; one that can't be read says so, and can still be added.</summary>
    private async Task ReadLinksAsync()
    {
        var previous = _reading;
        _reading = new CancellationTokenSource();
        var cancel = _reading.Token;
        await previous.CancelAsync();
        previous.Dispose();
        using var slots = new SemaphoreSlim(LinksAtOnce);
        await Task.WhenAll(_rows.Select(row => ReadAsync(row, _details, slots, cancel)));
    }

    private async Task ReadAsync((FileLink Link, TextBlock Name, TextBlock Size, TextBlock Figure, ProgressRing Reading, Border Tile, FontIcon Glyph) row, RequestDetails details, SemaphoreSlim slots, CancellationToken cancel)
    {
        try
        {
            await slots.WaitAsync(cancel);
            try
            {
                var (name, size) = await _read(row.Link, details, cancel);
                row.Name.Text = name;
                if (_links.Count == 1)
                    _name.PlaceholderText = name;
                row.Size.Text = _sites.NameOf(row.Link.SiteId) is { } plugin ? $"Read by {plugin}" : "";
                row.Size.ClearValue(TextBlock.ForegroundProperty);
                row.Figure.Text = size is { } bytes ? Units.Size(bytes) : "Size not known yet";
                row.Reading.IsActive = false;
                row.Reading.Visibility = Visibility.Collapsed;
                row.Glyph.Glyph = Glyphs.ForFile(name);
            }
            finally
            {
                slots.Release();
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // the dialog closed, or the links are being read again
        }
        catch (StageException e)
        {
            ShowNotRead(row.Size, row.Figure, row.Reading, e);
        }
#pragma warning disable CA1031 // whatever stopped this read, its row must stop saying it is reading, and the others go on
        catch (Exception e)
#pragma warning restore CA1031
        {
            CrashLog.Write("reading a link", e);
            ShowNotRead(row.Size, row.Figure, row.Reading, e);
        }
    }

    private void ShowNotRead(TextBlock size, TextBlock figure, ProgressRing reading, Exception why)
    {
        size.Text = why.Message;
        size.Foreground = ThemeBrushes.Get(ThemeBrushes.Critical, _theme);
        figure.Text = "";
        reading.IsActive = false;
        reading.Visibility = Visibility.Collapsed;
    }
}
