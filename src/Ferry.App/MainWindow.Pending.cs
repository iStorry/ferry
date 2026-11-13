using Ferry.App.Pages;
using Ferry.App.Services;
using Ferry.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Ferry.App;

/// <summary>What arrives for a page that isn't showing (links, albums, a search, copied links), held until it takes them.</summary>
public sealed partial class MainWindow
{
    private readonly List<LinkBatch> _pendingLinks = []; // for the Downloads page, which may not be showing, or for the server, which may not have answered
    private readonly List<string> _pendingAlbums = [];
    private string? _pendingSearch;
    private readonly HashSet<string> _offered = new(StringComparer.Ordinal);
    private List<string> _pendingOffer = [];

    /// <summary>Links from the command line or a second launch: queued on the Downloads page.</summary>
    /// <param name="details">what to send with the requests, when they came with a login (from the browser)</param>
    public void AddLinks(IReadOnlyCollection<string> links, Core.Http.RequestDetails? details = null)
    {
        if (links.Count == 0)
            return;
        if (ContentFrame.Content is DownloadsPage page && WelcomeFrame.Visibility == Visibility.Collapsed)
        {
            _ = page.AddGuardedAsync(links, details);
            return;
        }
        ParkLinks(new LinkBatch([.. links], details));
        TakeHeldLinksToDownloads();
    }

    /// <summary>Links to add once they can be: when the Downloads page shows, and the server has answered.</summary>
    public void ParkLinks(LinkBatch batch) => _pendingLinks.Add(batch);

    /// <summary>Links held go to the Downloads page, which says how adding them went. Still locked, or the session still opening, they wait for the page it opens on.</summary>
    private void TakeHeldLinksToDownloads()
    {
        if (_pendingLinks.Count == 0 || !_queueRead || WelcomeFrame.Visibility == Visibility.Visible)
            return;
        if (ContentFrame.Content is DownloadsPage page)
            _ = page.AddHeldLinksAsync();
        else
            ShowDownloads();
    }

    /// <summary>The links that waited for the server go in as it answers, whichever page is showing.</summary>
    private void OnStatusChanged()
    {
        if (_app.Queue.IsReady)
            TakeHeldLinksToDownloads();
    }

    /// <summary>Album links (pasted on Downloads): opened on the Browser page, one after another.</summary>
    public void OpenAlbums(IReadOnlyCollection<string> links)
    {
        if (links.Count == 0)
            return;
        if (ContentFrame.Content is BrowserPage page)
        {
            page.OpenAlbums(links);
            return;
        }
        _pendingAlbums.AddRange(links);
        Show(BrowserItem, new EntranceNavigationTransitionInfo());
    }

    /// <summary>A search from the command box: the Browser's albums for it.</summary>
    public void SearchAlbums(string search)
    {
        if (ContentFrame.Content is BrowserPage page)
        {
            page.SearchAlbums(search);
            return;
        }
        _pendingSearch = search;
        Show(BrowserItem, new EntranceNavigationTransitionInfo());
    }

    /// <summary>A search waiting for the Browser page, handed over once.</summary>
    public string? TakePendingSearch()
    {
        var search = _pendingSearch;
        _pendingSearch = null;
        return search;
    }

    /// <summary>Album links waiting for the Browser page, handed over once.</summary>
    public List<string> TakePendingAlbums()
    {
        var albums = new List<string>(_pendingAlbums);
        _pendingAlbums.Clear();
        return albums;
    }

    /// <summary>Links copied while the Downloads page wasn't showing, offered there once.</summary>
    public List<string> TakePendingOffer()
    {
        var offer = _pendingOffer;
        _pendingOffer = [];
        return offer;
    }

    /// <summary>With the setting on, copied links not yet in the queue are offered on the Downloads page.</summary>
    private async void OnClipboardChanged()
    {
        if (!_app.Settings.Boolean(SettingKey.WatchClipboard))
            return;
        // while another program holds the clipboard there is no text: the next copy is seen
        if (await ClipboardText.ReadAsync() is not { } text || _app.Queue.Sites is not { } sites)
            return;
        var queued = (_app.Queue.Queue?.Items ?? []).Select(i => i.Link.Key).ToHashSet(StringComparer.Ordinal);
        var links = text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => sites.TryParse(word, out var link, out _) ? link : null)
            .OfType<Core.Sites.FileLink>()
            .Where(link => !queued.Contains(link.Key) && _offered.Add(link.Key))
            .Select(link => link.Original.ToString())
            .ToList();
        if (links.Count == 0)
            return;
        if (ContentFrame.Content is DownloadsPage page && WelcomeFrame.Visibility == Visibility.Collapsed)
            page.Offer(links);
        else
            _pendingOffer = [.. _pendingOffer, .. links];
    }

    /// <summary>Links waiting for the Downloads page, handed over once.</summary>
    public List<LinkBatch> TakePendingLinks()
    {
        var batches = new List<LinkBatch>(_pendingLinks);
        _pendingLinks.Clear();
        return batches;
    }
}
