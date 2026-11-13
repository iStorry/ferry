using System.Diagnostics.CodeAnalysis;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.FileServers;

/// <summary>
/// Links to a file on an FTP or SFTP server. A password never goes in a link, which is saved in queue.json: it is given as
/// a "Password" header, kept encrypted with a file's other login details. SFTP logs in with the user's SSH keys.
/// </summary>
public sealed class FileServerSite(string userAgent) : ISite
{
    public const string SiteId = "file-server";

    private const string Sftp = "sftp";
    private const string PasswordHeader = "Password";
    private const int FtpPort = 21;

    private static readonly string[] Schemes = ["ftp", "ftps", Sftp];

    public string Id => SiteId;

    public string Name => "FTP and SFTP";

    public bool Claims(Uri link) => Schemes.Contains(link.Scheme);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = null;
        var user = UserOf(link) ?? "";
        reason = link.UserInfo.Contains(':', StringComparison.Ordinal) ? "A password can't go in the link, as links are saved. Give the link without it, and add a header \"Password: ...\" under Login."
            : link.AbsolutePath.EndsWith('/') ? $"\"{link}\" is a folder link. Ferry can't list FTP folders: add each file's own link."
            : HasControl(user) || HasControl(RemotePath(link)) || link.Host.StartsWith('-') || user.StartsWith('-') ? "This link has characters in it that can't be sent to a server."
            : null;
        if (reason is not null)
            return false;
        file = new FileLink(SiteId, link, LinkKeys.WithoutFragment(link));
        return true;
    }

    public async Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel)
    {
        var link = file.Original;
        IFileTransport transport;
        long? size = null;
        if (link.Scheme == Sftp)
        {
            transport = new SftpTransport(link, RemotePath(link));
        }
        else
        {
            var login = new FtpLogin(link.Host, link.Port > 0 ? link.Port : FtpPort, UserOf(link) ?? "anonymous", PasswordOf(details) ?? "ferry@", link.Scheme == "ftps");
            transport = new FtpTransport(login, RemotePath(link));
            size = await Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, async tryCancel =>
            {
                await using var ftp = await FtpClient.ConnectAsync(login, Stage.Resolve, tryCancel);
                return await ftp.SizeAsync(RemotePath(link), tryCancel);
            }, cancel);
        }
        return new ResolvedFile(file, file.Key, FileNames.Sanitize(file.Label), link, Referer: null, userAgent, ExpiresAt: null, IsWebPage: false, size, transport);
    }

    private static string RemotePath(Uri link) => Uri.UnescapeDataString(link.AbsolutePath);

    /// <summary>The user as the server will see it: a link's escaping hides nothing from the check.</summary>
    private static string? UserOf(Uri link) => link.UserInfo.Length > 0 ? Uri.UnescapeDataString(link.UserInfo) : null;

    private static string? PasswordOf(RequestDetails details) =>
        details.Headers is { } headers && headers.TryGetValue(PasswordHeader, out var password) ? password : null;

    private static bool HasControl(string text) => text.Any(char.IsControl);
}
