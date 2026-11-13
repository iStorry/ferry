using Ferry.Core;
using System.Formats.Tar;
using System.IO.Compression;
using Ferry.Core.Updates;

namespace Ferry.Plugins.Server;

/// <summary>
/// The agent build from GitHub, for a PC that has none next to it (the CLI on Windows or macOS): the Linux archive of
/// this very version's release, checked against its SHA256SUMS.txt, unpacked once into a cache folder.
/// </summary>
public static class AgentDownloads
{
    // this version has no release (a development build): once GitHub has said so, no reconnect asks again
    private static volatile bool s_noRelease;

    /// <summary>The agent build for <paramref name="rid"/>, downloaded if needed; null when this version has no release
    /// (a development build).</summary>
    /// <exception cref="StageException">GitHub couldn't be reached, or the file isn't the published one</exception>
    public static async Task<string?> FetchAsync(Releases releases, string rid, string cacheFolder, CancellationToken cancel)
    {
        var version = Product.Version;
        var target = Path.Combine(cacheFolder, $"ferry-{version}-{rid}");
        if (File.Exists(target))
            return target;
        if (s_noRelease)
            return null;
        var release = await releases.ForVersionAsync(version, cancel);
        if (release is null)
        {
            s_noRelease = true;
            return null;
        }
        var name = $"ferry-{version}-{rid}.tar.gz";
        if (release.Asset(name) is not { } asset)
            return null;
        var archive = await releases.DownloadAsync(release, asset, cacheFolder, cancel);
        try
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var tar = new TarReader(gzip);
            while (await tar.GetNextEntryAsync(copyData: false, cancel) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || Path.GetFileName(entry.Name) != "ferry"
                    || entry.DataStream is null)
                    continue;
                var temporary = target + ".tmp";
                await using (var output = File.Create(temporary))
                    await entry.DataStream.CopyToAsync(output, cancel);
                File.Move(temporary, target, overwrite: true);
                return target;
            }
            throw new StageException(Stage.Update, $"{name} has no ferry program in it.");
        }
        finally
        {
            File.Delete(archive);
        }
    }
}
