using System.Diagnostics;
using Ferry.App.Pages;
using Ferry.App.Services;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ferry.App;

/// <summary>
/// The box in the title bar (docs/mocks/add.html): links to add, things to do, places to go. Enter runs the top line. It lists
/// what can be done and found now, and keeps nothing that was typed or opened, so there is no list of recent ones.
/// </summary>
public sealed partial class MainWindow
{
    private const int MostFiles = 4;
    private const int MostSettings = 3;

    private void OnCommandAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (CommandArea.Visibility != Visibility.Visible)
            return;
        args.Handled = true;
        if (CommandBox.Focus(FocusState.Keyboard))
            OpenCommandList();
    }

    /// <summary>
    /// A click in the box lists what can be done, as Ctrl+K does. Not on focus: focus comes back to the box when a line has been
    /// chosen and falls on it as a page opens, and neither is asking for the list.
    /// </summary>
    private void OnCommandPressed(object sender, PointerRoutedEventArgs e) => OpenCommandList();

    private void OpenCommandList()
    {
        CommandBox.ItemsSource = Lines(CommandBox.Text);
        CommandBox.IsSuggestionListOpen = true;
    }

    /// <summary>What Enter will do with the text, under the box as it is typed or pasted.</summary>
    private void OnCommandTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // with text in the box its clear button is there instead
        CommandShortcut.Visibility = CommandBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            CommandBox.ItemsSource = Lines(CommandBox.Text);
    }

    private void OnCommandSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var line = args.ChosenSuggestion as CommandItem;
        if (line?.Run is null && args.QueryText.Trim().Length > 0)
            line = Lines(args.QueryText).FirstOrDefault(item => item.Run is not null);
        if (line?.Run is not { } run)
            return;
        sender.Text = "";
        sender.ItemsSource = null;
        sender.IsSuggestionListOpen = false;
        _ = Dialogs.GuardAsync(Root.XamlRoot, DidntWork, run);
    }

    private List<CommandItem> Lines(string text)
    {
        var words = text.Trim();
        var links = LinksIn(words);
        if (links.Length > 0)
            return LinkLines(links);
        var search = words.Length > 0 && _app.Browse.IsOffered;
        if (_app.IsDecoy)
            return [];
        if (words.Length == 0)
            return [.. Actions()];
        var lines = new List<CommandItem>();
        lines.AddRange(Actions().Concat(ByNameOnly()).Where(line => line.Title.Contains(words, StringComparison.OrdinalIgnoreCase)));
        lines.AddRange(Files(words));
        lines.AddRange(Settings(words));
        if (search)
            lines.Add(new CommandItem("\uE721", $"Search albums for \"{words}\"", Run: () => Done(() => SearchAlbums(words))));
        return lines;
    }

    /// <summary>The text as links to add, when any of it is one; none otherwise.</summary>
    private static string[] LinksIn(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(word => Uri.TryCreate(word, UriKind.Absolute, out _)) ? parts : [];
    }

    /// <summary>What Enter does with these links, then what each kind is and what reads it.</summary>
    private List<CommandItem> LinkLines(string[] links)
    {
        var sites = _app.Queue.Sites;
        var albums = links.Where(link => _app.Browse.TryReadPage(link, out var page, out _) && page.Kind == Ferry.Core.Browse.PageKind.Album).ToList();
        var count = links.Length - albums.Count;
        var off = links.Select(link => sites?.OffSiteOf(link)).OfType<Core.Sites.ISite>().FirstOrDefault();
        var lines = new List<CommandItem>
        {
            new(off is null ? "" : "",
                off is null ? count == 1 ? "Add this link" : $"Add {count} links" : $"{off.Name} is off",
                off is null ? _app.Settings.Boolean(SettingKey.AskBeforeAdding) ? "Shows each one's name and size first" : "" : $"Turn it on to download {(count == 1 ? "this" : "these")}",
                "Enter",
                () => Done(() => AddLinks(links))),
        };
        if (count == 0)
            lines.Clear(); // only albums: Enter opens them
        if (albums.Count > 0)
        {
            lines.Add(new CommandItem("\uF0E2", albums.Count == 1 ? "Open the album in the Browser" : $"Open {albums.Count} albums in the Browser",
                _app.Browse.SiteName, count == 0 ? "Enter" : "", () => Done(() => OpenAlbums(albums))));
        }
        if (sites is not null)
        {
            var kinds = links.Select(link => sites.TryParse(link, out var file, out _) ? sites.NameOf(file.SiteId) : null).OfType<string>()
                .GroupBy(name => name, StringComparer.Ordinal);
            foreach (var kind in kinds)
                lines.Add(new CommandItem("", kind.Key, kind.Count() == 1 ? "" : $"{kind.Count()} links"));
        }
        return lines;
    }

    /// <summary>What can be done or gone to from here, the ones that apply now first: the list at rest.</summary>
    private IEnumerable<CommandItem> Actions()
    {
        var queue = _app.Queue.Queue;
        var items = queue?.Items ?? [];
        yield return new CommandItem("\uE77F", "Add links from the clipboard", "", "Ctrl V", AddFromClipboardAsync);
        if (items.Any(item => item.State is QueueState.Waiting or QueueState.Resolving or QueueState.Downloading or QueueState.OnServer or QueueState.Transferring))
            yield return new CommandItem("\uE769", "Pause all", Run: () => queue!.PauseAllAsync());
        if (items.Any(item => item.State == QueueState.Paused))
            yield return new CommandItem("\uE768", "Resume all", Run: () => queue!.ResumeAllAsync());
        if (!SpeedLimits.IsLocked(_app.Settings))
            yield return new CommandItem("\uEC4A", "Set a speed limit", SpeedLimits.Label(_app.Settings), Run: () => SpeedLimits.AskAsync(Root.XamlRoot, _app));
        yield return new CommandItem("\uE838", "Open the download folder", "", "Ctrl Shift O", () => Done(OpenDownloadFolder));
        yield return new CommandItem("\uE896", "Go to Downloads", Run: () => Done(ShowDownloads));
        if (BrowserItem.Visibility == Visibility.Visible)
            yield return new CommandItem("\uF0E2", "Go to the Browser", Run: () => Done(ShowBrowser));
        if (LibraryItem.Visibility == Visibility.Visible)
            yield return new CommandItem("\uE7FC", "Go to the Library", Run: () => Done(ShowLibrary));
        if (ServerItem.Visibility == Visibility.Visible)
            yield return new CommandItem(Glyphs.Server, "Go to Server", Run: () => Done(() => ServerItem.IsChecked = true));
        yield return new CommandItem("\uEA86", "Go to Plugins", Run: () => Done(ShowPlugins));
        yield return new CommandItem("\uE713", "Go to Settings", Run: () => Done(ShowSettings));
        yield return new CommandItem("\uE946", "About Ferry", Run: () => Done(ShowAbout));
    }

    /// <summary>The list at rest is short; these show once they are asked for by name.</summary>
    private IEnumerable<CommandItem> ByNameOnly()
    {
        yield return new CommandItem(Glyphs.For(_app.Queue.Where), $"Where downloads run: {_app.Queue.WhereDisplay}", Run: () => Done(ShowWhere));
        foreach (var action in Enum.GetValues<WhenDone>())
            yield return new CommandItem("\uE708", $"When the queue finishes: {WhenDoneActions.InSentence(action)}", action == _app.Queue.WhenDone ? "Chosen now" : "",
                Run: () => Done(() => _app.Queue.WhenDone = action));
        yield return new CommandItem(PluginCatalog.BuiltInGlyph, "Links Ferry reads by itself", "Built in", Run: () => Done(() => ShowPlugin(PluginCatalog.BuiltIn)));
        foreach (var plugin in PluginCatalog.All)
            yield return new CommandItem(plugin.Glyph, plugin.Name, "Plugin", Run: () => Done(() => ShowPlugin(plugin.Name)));
    }

    private IEnumerable<CommandItem> Files(string words)
    {
        foreach (var item in (_app.Queue.Queue?.Items ?? []).Where(item => item.DisplayName.Contains(words, StringComparison.OrdinalIgnoreCase)).Take(MostFiles))
            yield return new CommandItem(Glyphs.ForFile(item.DisplayName), item.DisplayName, "In your queue", Run: () => Done(() => ShowFile(item.Id)));
    }

    private IEnumerable<CommandItem> Settings(string words)
    {
        foreach (var (key, section) in SettingsPage.Find(words).Take(MostSettings))
            yield return new CommandItem("\uE713", key.Title, "Setting", Run: () => Done(() => ShowSettings(section)));
    }

    /// <summary>Downloads, with this file in view.</summary>
    private void ShowFile(Guid id)
    {
        if (ContentFrame.Content is not DownloadsPage)
            ShowDownloads();
        (ContentFrame.Content as DownloadsPage)?.Reveal(id);
    }

    private async Task AddFromClipboardAsync()
    {
        if (await ClipboardText.ReadAsync() is { } text)
            AddLinks(LinkBatch.LinesOf(text));
        else
            await Dialogs.ShowErrorAsync(Root.XamlRoot, ClipboardText.ReadFailed, ClipboardText.Held);
    }

    private void OpenDownloadFolder()
    {
        try
        {
            var folder = Directory.CreateDirectory(_app.Settings.DownloadFolder).FullName;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\""))?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _ = Dialogs.ShowErrorAsync(Root.XamlRoot, "Couldn't open the folder", e.Message); // a drive that's gone, say
        }
    }

    private static Task Done(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
