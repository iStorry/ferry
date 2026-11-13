using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Sites;
using Ferry.Core.Tools;

namespace Ferry.Plugins.MediaSites;

/// <summary>
/// A video page on a listed site: yt-dlp says what it is called and fetches it. It refuses DRM by itself. yt-dlp runs twice
/// per file: the name has to be known before the download starts, as the part is named after it, and only then is it fetched.
/// </summary>
public sealed class MediaSite(string tool, string format, string userAgent, IReadOnlyList<string> hosts) : ISite
{
    public const string SiteId = "media";

    private const string NameMark = "ferry-name:";
    private const string SizeMark = "ferry-size:";
    private const string ProgressMark = "ferry:";
    private const string IfMissing = "Install it, then set where it is in Media Sites' options.";

    private readonly HostSet _hosts = HostSet.Under([.. hosts]);

    public string Id => SiteId;

    public string Name => "Media Sites";

    public bool Claims(Uri link) => _hosts.Claims(link);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = new FileLink(SiteId, link, LinkKeys.WithoutFragment(link));
        reason = null;
        return true;
    }

    /// <exception cref="StageException">yt-dlp isn't there, or it failed; says why</exception>
    public async Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        var (name, size) = ((string?)null, (long?)null);
        await ToolProcess.RunAsync(tool, ["--no-playlist", "--no-warnings", "--skip-download", "-f", format,
                                          "--print", $"{NameMark}%(title).150B.%(ext)s", "--print", $"{SizeMark}%(filesize,filesize_approx)d", "--", file.Original.ToString()],
                                   line =>
                                   {
                                       if (line.StartsWith(NameMark, StringComparison.Ordinal))
                                           name = line[NameMark.Length..].Trim();
                                       else if (line.StartsWith(SizeMark, StringComparison.Ordinal) && long.TryParse(line.AsSpan(SizeMark.Length), CultureInfo.InvariantCulture, out var bytes))
                                           size = bytes;
                                   }, Stage.Resolve, cancel, IfMissing);
        return new ResolvedFile(file, file.Key, FileNames.Sanitize(name is { Length: > 0 } ? name : file.Label), file.Original, Referer: null, userAgent, ExpiresAt: null, IsWebPage: false, size,
                                new YtDlpTransfer(tool, format, file.Original));
    }

    private sealed class YtDlpTransfer(string tool, string format, Uri link) : IFileTransport
    {
        public Task TransferAsync(TransportJob job, CancellationToken cancel)
        {
            var total = 0L;
            return ToolProcess.RunAsync(tool, ["--no-playlist", "--no-warnings", "--no-part", "--continue", "--newline", "-f", format, "-o", job.PartPath.Replace("%", "%%", StringComparison.Ordinal),
                                               "--progress-template", $"download:{ProgressMark}%(progress.downloaded_bytes)d %(progress.total_bytes,progress.total_bytes_estimate)d", "--", link.ToString()],
                                        line =>
                                        {
                                            if (!line.StartsWith(ProgressMark, StringComparison.Ordinal) || line[ProgressMark.Length..].Split(' ') is not [var done, var size])
                                                return;
                                            if (long.TryParse(size, CultureInfo.InvariantCulture, out var bytes) && bytes != total)
                                                job.OnTotal(total = bytes);
                                            if (long.TryParse(done, CultureInfo.InvariantCulture, out var written))
                                                job.OnBytes(written);
                                        }, Stage.Download, cancel, IfMissing);
        }
    }
}
