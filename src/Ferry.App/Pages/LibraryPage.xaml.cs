using System.Diagnostics;
using Ferry.App.Services;
using Ferry.Core.Library;
using Ferry.Core.Settings;
using Ferry.Plugins.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Ferry.App.Pages;

/// <summary>
/// The Library: what is in a folder of yours by name and version, found by name, and, where a plugin on your server can ask
/// its site, which are updated and which outdated. The folder is read on this PC; the site is only ever asked by the server.
/// </summary>
public sealed partial class LibraryPage : Page
{
    // searches at once on the server, so a big library doesn't ask the site for everything together
    private const int SearchesAtOnce = 2;

    private readonly AppServices _app = App.Services;
    private string? _readFrom; // the folder the list is of
    private int _reads; // a newer read replaces what an older one finds
    private IReadOnlyList<LibraryEntry> _games = [];
    private string? _problem;

    // the update check: what it found, by path, what it was for, and how it went
    private readonly Dictionary<string, TitleState> _updates = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _checking;
    private string? _checkedFor;
    private string _checkLine = "";

    public LibraryPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => ShowGames();
        Loaded += (_, _) =>
        {
            _app.SettingsChanged += Follow;
            Follow();
        };
        Unloaded += (_, _) =>
        {
            _app.SettingsChanged -= Follow;
            _checking?.Cancel();
        };
    }

    /// <summary>The plugin that looks titles up is installed and on.</summary>
    private bool IsOn => _app.LibraryPlugin is not null;

    private string Folder => _app.Settings.Text(SettingKey.LibraryFolder).Trim();

    /// <summary>What the update check depends on, besides the games: the site, and the server being there to ask it.</summary>
    private string CheckFor => $"{_app.LibraryPlugin?.Stored.Manifest.Id}|{_app.Settings.Mode}|{_app.Queue.Queue is RemoteQueue { IsAttached: true }}";

    /// <summary>The settings as they are now: another folder is read, another site or server checked against.</summary>
    private void Follow()
    {
        if (IsOn && Folder.Length > 0 && !string.Equals(Folder, _readFrom, StringComparison.OrdinalIgnoreCase))
        {
            _ = ReadAsync();
            return;
        }
        if (IsOn && _readFrom is not null && _problem is null && CheckFor != _checkedFor)
            _ = CheckAsync();
        ShowGames();
    }

    /// <summary>The folder, off the UI thread, with a quiet spinner while it is read; then the update check.</summary>
    private async Task ReadAsync()
    {
        var folder = Folder;
        var read = ++_reads;
        _readFrom = folder;
        _checking?.Cancel(); // of games that may be gone
        Busy(true);
        IReadOnlyList<LibraryEntry> games = [];
        string? problem = null;
        try
        {
            games = await Task.Run(() => LibraryFolder.Read(folder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problem = e is DirectoryNotFoundException ? "The folder isn't there any more." : $"The folder couldn't be read: {e.Message}";
        }
        if (read != _reads)
            return; // the folder changed meanwhile
        (_games, _problem) = (games, problem);
        Busy(false);
        ShowGames();
        if (problem is null)
            await CheckAsync();
    }

    private void Busy(bool busy)
    {
        Reading.IsActive = busy;
        Reading.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Each game with a version, or with parts, searched for by name on the update site, from the server and only from there,
    /// a couple at a time. What isn't on the site stays as it is; a version other than the site's newest is outdated.
    /// </summary>
    private async Task CheckAsync()
    {
        _checking?.Cancel();
        using var run = new CancellationTokenSource();
        _checking = run;
        try
        {
            await CheckAsync(run.Token);
        }
        finally
        {
            if (ReferenceEquals(_checking, run))
                _checking = null; // it is disposed next, and can't be cancelled after
        }
    }

    private async Task CheckAsync(CancellationToken cancel)
    {
        _checkedFor = CheckFor;
        _updates.Clear();
        var games = _games.Where(game => game.Version is not null || game.Parts.Any(part => part.Version is not null)).ToList();
        _checkLine = games.Count == 0 ? ""
            : _app.Settings.Mode != DownloadMode.Server ? "Updates are checked from your server, so only while downloads run there."
            : _app.Queue.Queue is not RemoteQueue { IsAttached: true } ? "Updates are checked once your server is connected. Refresh then."
            : $"Checking 0 of {games.Count} on your server…";
        ShowGames();
        if (games.Count == 0 || _app.Queue.Queue is not RemoteQueue { IsAttached: true } remote || _app.Settings.Mode != DownloadMode.Server)
            return;

        var search = new RemoteTitleSearch(remote);
        var (done, failed) = (0, 0);
        string? why = null;
        using var gate = new SemaphoreSlim(SearchesAtOnce);
        Busy(true);
        async Task CheckOneAsync(LibraryEntry game)
        {
            await gate.WaitAsync(cancel);
            try
            {
                // the whole name first; shorter only when that finds nothing that is this game
                List<FoundTitle> found = [];
                foreach (var words in TitleMatch.Searches(game.Name))
                {
                    found = [.. await search.SearchAsync(words, cancel)];
                    if (TitleMatch.Finds(game, found))
                        break;
                }
                _updates[game.Path] = TitleMatch.Check(game, null, found);
                foreach (var part in game.Parts)
                    _updates[part.Path] = TitleMatch.Check(game, part, found);
            }
            catch (Ferry.Core.StageException e)
            {
                (failed, why) = (failed + 1, e.Message);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                (failed, why) = (failed + 1, "the server didn't answer in time");
            }
            finally
            {
                gate.Release();
            }
            done++;
            if (cancel.IsCancellationRequested)
                return;
            _checkLine = $"Checking {done} of {games.Count} on your server…";
            CheckLine.Text = _checkLine;
            if (done % 10 == 0)
                ShowGames(); // what is known so far, without drawing the list for every game
        }
        try
        {
            await Task.WhenAll(games.Select(CheckOneAsync));
        }
        catch (OperationCanceledException)
        {
            // a newer read or check, or the page closed
        }
        if (cancel.IsCancellationRequested)
            return;
        Busy(false);
        var outdated = _updates.Values.Count(state => state == TitleState.Outdated);
        var updated = _updates.Values.Count(state => state == TitleState.UpToDate);
        var words = new List<string> { $"Checked: {updated} updated", $"{outdated} outdated" };
        if (failed > 0)
            words.Add($"{failed} couldn't be checked ({why})");
        _checkLine = string.Join(" · ", words);
        ShowGames();
    }

    private void ShowGames()
    {
        var on = IsOn;
        var hasFolder = Folder.Length > 0;
        var listing = on && hasFolder && _problem is null;
        FindBox.IsEnabled = RefreshButton.IsEnabled = on && hasFolder;
        var words = FindBox.Text.Trim();
        var shown = listing ? _games.Select(game => Matching(game, words)).OfType<LibraryEntry>().ToList() : [];
        Count.Text = !listing || _games.Count == 0 ? ""
            : shown.Count == _games.Count ? $"{_games.Count} title{(_games.Count == 1 ? "" : "s")}"
            : $"{shown.Count} of {_games.Count} titles";

        GameRows.Children.Clear();
        foreach (var game in shown)
        {
            GameRows.Children.Add(GameCard(game));
            foreach (var part in game.Parts)
                GameRows.Children.Add(PartCard(part));
        }
        SettingRows.MarkLines(GameRows);
        GamesCard.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Empty.Text = !on ? "The plugin that checks your library is off."
            : !hasFolder ? "Choose the folder your library is in: each title in a folder or archive of its own, named with its version, as in \"Name (v1.2)\"."
            : _problem is { } problem ? problem
            : Reading.IsActive ? ""
            : _games.Count == 0 ? "Nothing in this folder: each title is a folder or archive in it."
            : shown.Count == 0 ? $"Nothing matches \"{words}\"."
            : "";
        SettingsLink.Visibility = !on || !hasFolder || _problem is not null ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = Empty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CheckLine.Text = listing && _games.Count > 0 ? _checkLine : "";
        CheckLine.Visibility = CheckLine.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// A game as the words find it: all of it when its own name matches, otherwise only the parts that do, and null when
    /// nothing of it does.
    /// </summary>
    private static LibraryEntry? Matching(LibraryEntry game, string words)
    {
        bool Matches(LibraryEntry it) => $"{it.Name} {it.Version}".Contains(words, StringComparison.CurrentCultureIgnoreCase);
        if (words.Length == 0 || Matches(game))
            return game;
        var parts = game.Parts.Where(Matches).ToList();
        return parts.Count > 0 ? game with { Parts = parts } : null;
    }

    /// <summary>A game: its name, its version or how many parts it has, what it is and when it changed, and a way to it in Explorer.</summary>
    private Border GameCard(LibraryEntry game)
    {
        var pill = game.Parts.Count > 0 && game.Version is null
            ? Pill.Make(_games.FirstOrDefault(all => all.Path == game.Path)?.Parts.Count is { } count && count != game.Parts.Count
                ? $"{game.Parts.Count} of {count} parts" : $"{game.Parts.Count} part{(game.Parts.Count == 1 ? "" : "s")}", ActualTheme)
            : null;
        return Card(game, pill, Microsoft.UI.Text.FontWeights.SemiBold, indent: false);
    }

    /// <summary>A part of a game, such as "Season 1 (v1.0)", set in under it.</summary>
    private Border PartCard(LibraryEntry part) => Card(part, null, Microsoft.UI.Text.FontWeights.Normal, indent: true);

    private Border Card(LibraryEntry game, Border? pill, Windows.UI.Text.FontWeight weight, bool indent)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (indent)
            title.Children.Add(new FontIcon { Glyph = "", FontSize = 10, Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, ActualTheme), VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Text = game.Name, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(name, game.Path);
        title.Children.Add(name);
        title.Children.Add(pill ?? (game.Version is { } version
            ? Pill.Make(version, ActualTheme, ThemeBrushes.Accent)
            : Pill.Make("No version", ActualTheme)));
        switch (_updates.GetValueOrDefault(game.Path))
        {
            case TitleState.Outdated:
                title.Children.Add(Pill.Make("Outdated", ActualTheme, ThemeBrushes.Caution));
                break;
            case TitleState.UpToDate:
                title.Children.Add(Pill.Make("Updated", ActualTheme, ThemeBrushes.Success));
                break;
        }
        var show = new Button { Content = "Show", Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        show.Click += (_, _) => Show(game.Path, game.IsFolder);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(show, $"Show {game.Name} in Explorer");
        var what = game.IsFolder ? "Folder" : $"{Path.GetExtension(game.Path).TrimStart('.').ToUpperInvariant()} archive";
        var card = SettingRows.Card(title, $"{what} · changed {game.Changed:d MMM yyyy}", show);
        if (indent)
            card.Padding = new Thickness(36, 8, 16, 8);
        return card;
    }

    /// <summary>A folder opened in Explorer; an archive selected in its folder.</summary>
    private void Show(string path, bool isFolder)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", isFolder ? $"\"{path}\"" : $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            (Problem.Title, Problem.Message, Problem.IsOpen) = ("Explorer didn't open", e.Message, true);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (IsOn && Folder.Length > 0)
            _ = ReadAsync();
    }

    /// <summary>The settings of the plugin that asks the site: its page under Plugins.</summary>
    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_app.LibraryPlugin is { } plugin)
            App.Window?.ShowPlugin(plugin.Plugin.Name);
        else
            App.Window?.ShowPlugins();
    }

    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window!.Handle);
        if (await picker.PickSingleFolderAsync() is not { } folder)
            return;
        try
        {
            _app.Change(store => store.Set(SettingKey.LibraryFolder.Name, folder.Path));
        }
        catch (SettingsException ex)
        {
            (Problem.Title, Problem.Message, Problem.IsOpen) = ("The folder wasn't kept", ex.Message, true);
        }
    }

    private void OnFindChanged(object sender, TextChangedEventArgs e) => ShowGames();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => PageLayout.Padding = PageEdges.For(e.NewSize.Width);
}
