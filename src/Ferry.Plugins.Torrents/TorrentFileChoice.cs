using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Sites;
using MonoTorrent;

namespace Ferry.Plugins.Torrents;

/// <summary>A file in a torrent, by its place in the torrent's list.</summary>
public sealed record TorrentFileEntry(int Index, string Path, long Size);

/// <summary>
/// Torrent links, and which of their files are wanted. That is kept in the link's fragment ("#files=0,2"), which goes in the
/// queue with it and is never sent anywhere, so a restart and a server both know it.
/// </summary>
public static class TorrentFileChoice
{
    private const string MagnetScheme = "magnet";
    private const string FileEnding = ".torrent";
    private const string ChoiceMark = "#files=";
    private const long MaxFileBytes = 16 << 20;

    public static bool IsMagnet(Uri link) => link.Scheme == MagnetScheme;

    public static bool IsTorrent(Uri link) =>
        IsMagnet(link) || (LinkProbe.IsWebLink(link) && link.AbsolutePath.EndsWith(FileEnding, StringComparison.OrdinalIgnoreCase));

    public static bool TryReadMagnet(Uri link, [NotNullWhen(true)] out MagnetLink? magnet) =>
        MagnetLink.TryParse(link.ToString().Split('#')[0], out magnet);

    /// <summary>The places of the files wanted, or null for all of them.</summary>
    public static IReadOnlySet<int>? ChosenFiles(Uri link)
    {
        if (!link.Fragment.StartsWith(ChoiceMark, StringComparison.Ordinal))
            return null;
        var chosen = new HashSet<int>();
        foreach (var word in link.Fragment[ChoiceMark.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(word, CultureInfo.InvariantCulture, out var index))
                chosen.Add(index);
        }
        return chosen;
    }

    /// <summary>The link saying which files are wanted; as it was when that is all of them.</summary>
    public static string Chosen(Uri link, IEnumerable<int> indexes, int all)
    {
        var chosen = indexes.ToList();
        var bare = link.ToString().Split('#')[0];
        return chosen.Count == all ? bare : $"{bare}{ChoiceMark}{string.Join(',', chosen)}";
    }

    /// <summary>What is in the torrent; from peers for a magnet, so this may take as long as they do.</summary>
    /// <exception cref="StageException">it can't be read</exception>
    public static async Task<IReadOnlyList<TorrentFileEntry>> ReadAsync(HttpFetcher http, string userAgent, Uri link, RequestDetails details, CancellationToken cancel)
    {
        Torrent torrent;
        if (IsMagnet(link))
        {
            if (!TryReadMagnet(link, out var magnet))
                throw new StageException(Stage.Link, "This magnet link can't be read.");
            await using var lease = TorrentEngine.Lease();
            var bytes = await lease.Engine.DownloadMetadataAsync(magnet, cancel);
            torrent = Read(bytes.ToArray());
        }
        else
        {
            torrent = await FetchAsync(http, userAgent, link, details, cancel);
        }
        return [.. torrent.Files.Select((file, index) => new TorrentFileEntry(index, file.Path.ToString(), file.Length))];
    }

    /// <summary>The torrent file a link leads to.</summary>
    /// <exception cref="StageException">it can't be fetched, or isn't one</exception>
    public static Task<Torrent> FetchAsync(HttpFetcher http, string userAgent, Uri link, RequestDetails details, CancellationToken cancel) =>
        Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, async tryCancel =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            request.Headers.UserAgent.ParseAdd(userAgent);
            details.ApplyTo(request);
            using var response = await http.SendAsync(request, Stage.Resolve, tryCancel, asFile: true);
            return Read(await HttpFetcher.ReadBytesAsync(response, MaxFileBytes, Stage.Resolve, tryCancel));
        }, cancel);

    private static Torrent Read(byte[] bytes) =>
        Torrent.TryLoad(bytes, out var torrent) ? torrent : throw new StageException(Stage.Resolve, "This link doesn't lead to a torrent file Ferry can read.");
}
