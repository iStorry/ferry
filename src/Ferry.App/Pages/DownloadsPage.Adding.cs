using Ferry.App.Services;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.Official;
using Ferry.Plugins.Torrents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Ferry.App.Pages;

/// <summary>How links come to the queue: the command box, a paste, a drop, a copied link offered, a second launch.</summary>
public sealed partial class DownloadsPage
{
    // beyond this, a folder's files are added without each being read first in the Add dialog
    private const int MostLinksToRead = 100;

    private const string NotAdded = "Not added";

    private List<string> _offered = [];
    private Guid? _scrollTo; // the first of the files just added: brought into view when the list has it

    /// <summary>Copied links (with the clipboard setting on): offered, added on a click.</summary>
    public void Offer(IReadOnlyCollection<string> links)
    {
        _offered = [.. _offered.Union(links)];
        ClipboardWords.Text = _offered.Count == 1 ? $"Copied {_offered[0]}" : $"Copied {_offered.Count} links";
        ToolTipService.SetToolTip(ClipboardWords, string.Join('\n', _offered.Take(10)));
        ClipboardOffer.Visibility = Visibility.Visible;
    }

    private void OnCloseOffer(object sender, RoutedEventArgs e)
    {
        ClipboardOffer.Visibility = Visibility.Collapsed;
        _offered = [];
    }

    private async void OnAddOffered(object sender, RoutedEventArgs e)
    {
        ClipboardOffer.Visibility = Visibility.Collapsed;
        var links = _offered;
        _offered = [];
        await AddGuardedAsync(links);
    }

    /// <summary><see cref="AddLinksAsync"/> from where nothing is waiting for it: what goes wrong is said here.</summary>
    public Task AddGuardedAsync(IEnumerable<string> links, RequestDetails? details = null, string? album = null) =>
        Dialogs.GuardAsync(XamlRoot, NotAdded, () => AddLinksAsync(links, details, album));

    /// <summary>The links the window holds for this page, each batch on its own so that what came with one stays with it.</summary>
    public async Task AddHeldLinksAsync()
    {
        foreach (var batch in App.Window?.TakePendingLinks() ?? [])
            await AddGuardedAsync(batch.Lines, batch.Details, batch.Album);
    }

    /// <summary>
    /// Links to queue (from the command box, a paste, a drop, a second launch). A link of a plugin that is off offers to
    /// turn it on first, rather than being refused or saved as a web page; album links go on to the Browser once the files are in.
    /// </summary>
    /// <param name="details">what to send with the requests, when the links came with a login (from the browser)</param>
    /// <param name="album">a folder of their own for them, when they are a folder's files</param>
    private async Task AddLinksAsync(IEnumerable<string> links, RequestDetails? details = null, string? album = null)
    {
        List<string> lines = [.. links];
        if (lines.Count == 0)
            return;
        // nothing is read before the server is reached: in server mode it reads links to many files and the files' names
        if (!_app.Queue.IsReady)
        {
            Park(new LinkBatch(lines, details, album));
            return;
        }
        var lists = await ExpandAsync(lines, details);
        await OfferPluginAsync([.. lists.SelectMany(list => list.Links)]);
        List<string> albums = [];
        foreach (var list in lists)
        {
            List<string> files = [.. list.Links];
            albums.AddRange(TakeAlbums(files));
            await ChooseTorrentFilesAsync(files, details);
            await AddFilesAsync(files, list.Title ?? album, details);
        }
        // after the files: opening the Browser leaves this page
        App.Window?.OpenAlbums(albums);
    }

    /// <summary>The links as files, in lists: those that lead to many are read now, and any that can't be are said.</summary>
    private async Task<IReadOnlyList<FileList>> ExpandAsync(List<string> lines, RequestDetails? details)
    {
        if (_app.Queue.Expander is not { } expander)
            return [new FileList(null, lines)];
        (ReadingLinks.Visibility, ReadingRing.IsActive) = (Visibility.Visible, true);
        try
        {
            var expanded = await expander.ExpandAsync(lines, details ?? RequestDetails.None, CancellationToken.None);
            if (expanded.Problems.Count > 0)
                Show(InfoBarSeverity.Error, NotAdded, string.Join('\n', expanded.Problems.Select(problem => problem.Reason)));
            return expanded.Lists;
        }
        finally
        {
            (ReadingLinks.Visibility, ReadingRing.IsActive) = (Visibility.Collapsed, false);
        }
    }

    /// <summary>Links that came while the server wasn't reached: the window keeps them, and they go in once it is.</summary>
    private void Park(LinkBatch batch)
    {
        App.Window?.ParkLinks(batch);
        Show(InfoBarSeverity.Warning, "Not connected yet", "They are added once the server answers.");
    }

    /// <summary>
    /// Each torrent among <paramref name="lines"/>, with the files wanted from it: a cancelled one leaves the list. Not in server
    /// mode, where the server has no Torrents plugin and nothing is read here.
    /// </summary>
    private async Task ChooseTorrentFilesAsync(List<string> lines, RequestDetails? details)
    {
        // reading a torrent's files means joining its swarm, which in server mode is for the server to do, not this PC
        if (ServerMode || !_app.Settings.Boolean(TorrentKeys.AskFiles) || !TorrentsPlugin.Plugin.IsOn(_app.Settings))
            return;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!Uri.TryCreate(lines[i], UriKind.Absolute, out var link) || !TorrentFileChoice.IsTorrent(link))
                continue;
            if (await TorrentFilesDialog.ShowAsync(XamlRoot, link, _app.Settings, details ?? RequestDetails.None) is { } chosen)
                lines[i] = chosen;
            else
                lines.RemoveAt(i);
        }
    }

    /// <summary>Links of a plugin that is off: say so, and turn it on if that is what is wanted.</summary>
    private async Task OfferPluginAsync(List<string> lines)
    {
        if (_app.Queue.Sites is not { } sites)
            return;
        var off = lines.Select(sites.OffSiteOf).OfType<ISite>().Select(site => site.Id).ToList();
        foreach (var plugin in OfficialSites.Plugins)
        {
            if (await AsksForAsync(plugin.Name, off.Count(plugin.Owns)))
                await PluginSwitch.SetAsync(XamlRoot, _app, plugin, on: true);
        }
    }

    /// <returns>whether the user wants the plugin on, for the <paramref name="count"/> links that need it</returns>
    private async Task<bool> AsksForAsync(string plugin, int count) =>
        count > 0 && await Dialogs.ConfirmAsync(XamlRoot, $"Turn on {plugin}?", count == 1
            ? $"This link needs {plugin}, which is off. Turn it on to download it. Without it, Ferry would save the web page instead of the file."
            : $"{count} of these links need {plugin}, which is off. Turn it on to download them. Without it, Ferry would save the web pages instead of the files.",
            "Turn on", "Not now");

    /// <summary>Album links leave <paramref name="lines"/> for the Browser page, which lists their files to choose from.</summary>
    private List<string> TakeAlbums(List<string> lines)
    {
        if (!_app.Browse.IsOffered)
            return [];
        var albums = lines.Where(line => _app.Browse.TryReadPage(line, out var page, out _) && page.Kind == Ferry.Core.Browse.PageKind.Album).ToList();
        lines.RemoveAll(albums.Contains);
        return albums;
    }

    /// <param name="album">a folder of their own for them, when they are a folder's files</param>
    private async Task AddFilesAsync(List<string> lines, string? album = null, RequestDetails? details = null)
    {
        const string TooManyToRead = "Reading each one first would take a while, so they are added without a check.";
        if (lines.Count == 0)
            return;
        if (!_app.Queue.IsReady || Queue is not { } queue)
        {
            Park(new LinkBatch(lines, details, album)); // the server went away while the links were read
            return;
        }
        var options = new AddOptions(Details: details, Album: album);
        if (_app.Settings.Boolean(SettingKey.AskBeforeAdding) && _app.Queue.Sites is { } sites && NewLinks(lines, sites, queue) is { Count: > 0 } fresh)
        {
            if (fresh.Count > MostLinksToRead)
            {
                if (!await Dialogs.ConfirmAsync(XamlRoot, $"Add {fresh.Count} downloads?", album is null ? TooManyToRead : $"All of \"{album}\". {TooManyToRead}", "Add", "Cancel"))
                    return;
            }
            else if (await AddLinksDialog.ShowAsync(XamlRoot, sites, fresh, _app.Settings.DownloadFolder, ActualTheme, _app.Queue.ReadLinkAsync, onServer: ServerMode) is { } chosen)
            {
                options = chosen with { Details = chosen.Details is { IsEmpty: false } ? chosen.Details : details, Album = chosen.Folder is null ? album : null };
            }
            else
            {
                return; // cancelled
            }
        }
        QueueAddResult result;
        try
        {
            result = await queue.AddAsync(lines, options);
        }
        catch (Exception ex) when (ex is StageException or InvalidOperationException)
        {
            Show(InfoBarSeverity.Error, NotAdded, ex.Message);
            return;
        }
        ReportAdded(result);
    }

    /// <summary>The links among <paramref name="lines"/> that would be added; what isn't one is left for the queue to report.</summary>
    private static List<FileLink> NewLinks(List<string> lines, SiteCatalog sites, IDownloadQueue queue)
    {
        var queued = queue.Items.Select(i => (i.Link.SiteId, i.Link.Key)).ToHashSet();
        var links = new List<FileLink>();
        foreach (var line in lines)
        {
            if (sites.TryParse(line, out var link, out _) && queued.Add((link.SiteId, link.Key)))
                links.Add(link);
        }
        return links;
    }

    private void ReportAdded(QueueAddResult result)
    {
        if (result.Rejected.Count > 0)
        {
            // what wasn't added goes back to the command box, to fix
            App.Window?.SetCommandText(string.Join(" ", result.Rejected.Select(r => r.Text)));
            var given = result.Added.Count + result.Duplicates.Count + result.Rejected.Count;
            Show(InfoBarSeverity.Warning, $"{result.Rejected.Count} of {given} link{(given == 1 ? "" : "s")} {(result.Rejected.Count == 1 ? "wasn't" : "weren't")} added",
                 string.Join(" ", result.Rejected.Select(r => r.Reason).Distinct()));
        }
        else if (result.Duplicates.Count > 0)
        {
            Show(InfoBarSeverity.Informational, $"{result.Duplicates.Count} already in the queue", "Each file is downloaded once.");
        }
        else
        {
            AddResult.IsOpen = false; // the new rows say so
        }
        // the first new row in view, so adding to a long list visibly does something
        if (result.Added.Count > 0)
        {
            _scrollTo = result.Added[0].Id;
            ShowNews();
            ScrollToAdded();
        }
    }

    private async void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
            return; // the text box pastes for itself
        args.Handled = true;
        await Dialogs.GuardAsync(XamlRoot, NotAdded, AddFromClipboardAsync);
    }

    private async Task AddFromClipboardAsync()
    {
        if (await ClipboardText.ReadAsync() is { } text)
            await AddLinksAsync(LinkBatch.LinesOf(text));
        else
            Show(InfoBarSeverity.Warning, ClipboardText.ReadFailed, ClipboardText.Held);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Text) || e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to the queue";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e) => await Dialogs.GuardAsync(XamlRoot, NotAdded, () => AddDroppedAsync(e.DataView));

    private async Task AddDroppedAsync(DataPackageView dropped)
    {
        var text = new List<string>();
        try
        {
            if (dropped.Contains(StandardDataFormats.Text))
                text.Add(await dropped.GetTextAsync());
            if (dropped.Contains(StandardDataFormats.StorageItems))
            {
                foreach (var file in (await dropped.GetStorageItemsAsync()).OfType<StorageFile>().Where(f => f.FileType is ".txt" or ".url"))
                    text.Add(await File.ReadAllTextAsync(file.Path)); // any encoding with a BOM, else UTF-8
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            Show(InfoBarSeverity.Warning, "Couldn't read what was dropped", ex.Message);
            return;
        }
        if (text.Count > 0)
            await AddLinksAsync(text.SelectMany(LinkBatch.LinesOf));
    }
}
