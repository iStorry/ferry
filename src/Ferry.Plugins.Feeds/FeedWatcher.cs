using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Feeds;

/// <summary>
/// Reads the feeds now and then and hands over what is new. What was seen is kept as hashes, one a line, so the file
/// doesn't say what was read.
/// </summary>
/// <param name="seenFile">where what was seen is kept</param>
/// <param name="add">adds links to the queue</param>
/// <param name="problem">told about a feed that couldn't be read; the others carry on</param>
public sealed class FeedWatcher(HttpFetcher http, string seenFile, Func<IReadOnlyList<string>, Task> add, Action<string, Exception> problem)
{
    private readonly HashSet<string> _seen = [];
    private bool _loaded;

    /// <summary>Read the feeds, then wait, until <paramref name="cancel"/>.</summary>
    public async Task RunAsync(Func<FerrySettings> settings, CancellationToken cancel)
    {
        await LoadAsync(cancel);
        while (!cancel.IsCancellationRequested)
        {
            var now = settings();
            await ReadAllAsync(now, cancel);
            await Task.Delay(now.Duration(FeedsPlugin.Interval), cancel);
        }
    }

    private async Task ReadAllAsync(FerrySettings settings, CancellationToken cancel)
    {
        var (fresh, room) = (new List<string>(), settings.Number(FeedsPlugin.MostAtOnce));
        foreach (var feed in settings.Text(FeedsPlugin.Urls).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (Uri.TryCreate(feed, UriKind.Absolute, out var url) && LinkProbe.IsWebLink(url))
                    Take(feed, FeedReader.Files(await HtmlLinks.ReadPageAsync(http, settings.BrowserUserAgent, url, RequestDetails.None, cancel)), fresh, room);
            }
            catch (Exception e) when (e is StageException or XmlException)
            {
                problem("reading a feed", e);
            }
        }
        if (fresh.Count > 0)
            await add(fresh);
        await SaveAsync(cancel);
    }

    /// <summary>A feed read for the first time only says what is on it; after that, what wasn't seen is new, up to the room there is.</summary>
    private void Take(string feed, IReadOnlyList<Uri> files, List<string> fresh, int room)
    {
        var first = _seen.Add(Hash($"feed:{feed}"));
        foreach (var file in files.Select(file => file.ToString()))
        {
            if (first)
                _seen.Add(Hash(file));
            else if (fresh.Count < room && _seen.Add(Hash(file)))
                fresh.Add(file);
        }
    }

    private async Task LoadAsync(CancellationToken cancel)
    {
        if (_loaded || !File.Exists(seenFile))
            return;
        _seen.UnionWith(await File.ReadAllLinesAsync(seenFile, cancel));
        _loaded = true;
    }

    private Task SaveAsync(CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(seenFile) ?? ".");
        return File.WriteAllLinesAsync(seenFile, _seen, cancel);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
