using System.Diagnostics.CodeAnalysis;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Sites;
using MonoTorrent;

namespace Ferry.Plugins.Torrents;

/// <summary>Magnet links, and links to a .torrent file. A magnet says little until peers are found, so its size comes during the download.</summary>
/// <param name="claimsFiles">whether a link to a .torrent file is to be read as a torrent: with the plugin off, it is a file like any other</param>
/// <param name="downloadLimit">bytes per second, 0 for none</param>
/// <param name="uploadLimit">bytes per second, 0 for none</param>
/// <param name="packFolders">a torrent of many files ends as one .tar, for a server, whose files go on to the PC one at a time</param>
public sealed class TorrentSite(HttpFetcher http, string userAgent, bool claimsFiles, long downloadLimit, long uploadLimit, bool packFolders = false) : ISite
{
    public const string SiteId = "torrent";

    public string Id => SiteId;

    public string Name => "Torrents";

    public bool Claims(Uri link) =>
        TorrentFileChoice.IsMagnet(link) || (claimsFiles && TorrentFileChoice.IsTorrent(link));

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = null;
        reason = null;
        if (!TorrentFileChoice.IsMagnet(link))
        {
            file = new FileLink(SiteId, link, LinkKeys.WithoutFragment(link));
            return true;
        }
        if (!TorrentFileChoice.TryReadMagnet(link, out var magnet))
        {
            reason = $"\"{link}\" is not a magnet link Ferry can read.";
            return false;
        }
        var hash = magnet.InfoHashes.V1OrV2.ToHex();
        file = new FileLink(SiteId, link, hash, magnet.Name ?? hash);
        return true;
    }

    public async Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        if (TorrentFileChoice.IsMagnet(file.Original))
        {
            if (!TorrentFileChoice.TryReadMagnet(file.Original, out var magnet))
                throw new StageException(Stage.Link, "This magnet link can't be read.");
            return Resolved(file, file.Key, file.Label, size: null, (engine, folder, settings) => engine.AddAsync(magnet, folder, settings));
        }
        var torrent = await TorrentFileChoice.FetchAsync(http, userAgent, file.Original, details, cancel);
        return Resolved(file, torrent.InfoHashes.V1OrV2.ToHex(), torrent.Name, TorrentFileChoice.ChosenFiles(file.Original) is null ? torrent.Size : null,
                        (engine, folder, settings) => engine.AddAsync(torrent, folder, settings));
    }

    private ResolvedFile Resolved(FileLink file, string id, string name, long? size, TorrentTransport.Adding add) =>
        new(file, id, FileNames.Sanitize(name), file.Original, Referer: null, userAgent, ExpiresAt: null, IsWebPage: false, size,
            new TorrentTransport(add, TorrentFileChoice.ChosenFiles(file.Original), downloadLimit, uploadLimit, packFolders));
}
