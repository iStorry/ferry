using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Ferry.Core;
using Ferry.Core.Files;
using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Mega;

/// <summary>
/// Mega's file links ("/file/{handle}#{key}", or the older "/#!{handle}!{key}"), and a file in a folder
/// ("/folder/{folder}#{key}/file/{handle}"). The key stays here: it is in the part of a link never sent, and only decrypts
/// what the API points at.
/// </summary>
public sealed partial class MegaSite(HttpFetcher http, string userAgent) : ISite
{
    public const string SiteId = "mega";

    private static readonly HostSet Hosts = HostSet.Of("mega.nz", "mega.co.nz");

    private readonly MegaApi _api = new(http, userAgent);

    public string Id => SiteId;

    public string Name => "Mega";

    public bool Claims(Uri link) => IsMega(link);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        file = null;
        if (InFolder().Match(link.AbsolutePath + link.Fragment) is { Success: true } inFolder)
        {
            file = new FileLink(SiteId, link, inFolder.Groups["handle"].Value, "Mega file");
            reason = null;
            return true;
        }
        if (TryRead(link, out var handle, out _))
        {
            file = new FileLink(SiteId, link, handle, "Mega file");
            reason = null;
            return true;
        }
        reason = !IsFolder(link) ? $"\"{link}\" is not a Mega file link with its key."
            : TryReadFolder(link, out _, out _) ? $"\"{link}\" is a folder link. Folders are added through {MegaPlugin.Plugin.Name}."
            : $"\"{link}\" is a Mega folder link whose key can't be read.";
        return false;
    }

    public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel) =>
        Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, async tryCancel =>
        {
            var (rawKey, inFolder) = await KeyAsync(file, tryCancel);
            var (url, size, attributes) = await _api.FileAsync(file.Key, inFolder, tryCancel);
            var name = MegaNames.NameOf(attributes, MegaKeys.Split(rawKey).Key) ?? file.Label;
            return new ResolvedFile(file, file.Key, FileNames.Sanitize(name), url, Referer: null, userAgent, ExpiresAt: null, IsWebPage: false, size,
                                    new MegaTransport(http, userAgent, url, rawKey, size));
        }, cancel);

    /// <summary>The file's key: in its own link, or for a file in a folder, among what the folder's key opens.</summary>
    private async Task<(byte[] Key, string? InFolder)> KeyAsync(FileLink file, CancellationToken cancel)
    {
        if (InFolder().Match(file.Original.AbsolutePath + file.Original.Fragment) is not { Success: true } inFolder)
            return TryRead(file.Original, out _, out var rawKey) ? (rawKey, null) : throw new StageException(Stage.Link, "This Mega link has no key.");
        var folder = inFolder.Groups["folder"].Value;
        if (!TryDecode(inFolder.Groups["key"].Value, MegaKeys.AesKeyBytes, out var folderKey))
            throw new StageException(Stage.Link, "This Mega link's key can't be read.");
        var nodes = await _api.FolderAsync(folder, folderKey, cancel);
        return nodes.FirstOrDefault(node => node.IsFile && node.Handle == file.Key) is { } found
            ? (found.RawKey, folder)
            : throw new StageException(Stage.Resolve, "This file isn't in its Mega folder any more.");
    }

    internal static bool IsMega(Uri link) => Hosts.Claims(link);

    internal static bool IsFolder(Uri link) =>
        link.AbsolutePath.StartsWith("/folder/", StringComparison.Ordinal) || link.Fragment.StartsWith("#F!", StringComparison.Ordinal);

    /// <summary>A link to a whole folder: its handle and key.</summary>
    internal static bool TryReadFolder(Uri link, [NotNullWhen(true)] out string? folder, [NotNullWhen(true)] out byte[]? key)
    {
        (folder, key) = (null, null);
        var match = WholeFolder().Match(link.AbsolutePath + link.Fragment);
        if (!match.Success)
            match = OldFolder().Match(link.Fragment);
        if (!match.Success || !TryDecode(match.Groups["key"].Value, MegaKeys.AesKeyBytes, out var decoded))
            return false;
        (folder, key) = (match.Groups["folder"].Value, decoded);
        return true;
    }

    private static bool TryRead(Uri link, out string handle, out byte[] rawKey)
    {
        (handle, rawKey) = ("", []);
        var match = NewLink().Match(link.AbsolutePath + link.Fragment);
        if (!match.Success)
            match = OldLink().Match(link.Fragment);
        if (!match.Success || !TryDecode(match.Groups["key"].Value, MegaKeys.RawFileKeyBytes, out var decoded))
            return false;
        (handle, rawKey) = (match.Groups["handle"].Value, decoded);
        return true;
    }

    private static bool TryDecode(string text, int length, out byte[] bytes)
    {
        try
        {
            bytes = Base64Url.DecodeFromChars(text);
            return bytes.Length == length;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    [GeneratedRegex("^/file/(?<handle>[A-Za-z0-9_-]+)#(?<key>[A-Za-z0-9_-]+)$")]
    private static partial Regex NewLink();

    [GeneratedRegex("^#!(?<handle>[A-Za-z0-9_-]+)!(?<key>[A-Za-z0-9_-]+)$")]
    private static partial Regex OldLink();

    [GeneratedRegex("^/folder/(?<folder>[A-Za-z0-9_-]+)#(?<key>[A-Za-z0-9_-]+)/file/(?<handle>[A-Za-z0-9_-]+)$")]
    private static partial Regex InFolder();

    [GeneratedRegex("^/folder/(?<folder>[A-Za-z0-9_-]+)#(?<key>[A-Za-z0-9_-]+)$")]
    private static partial Regex WholeFolder();

    [GeneratedRegex("^#F!(?<folder>[A-Za-z0-9_-]+)!(?<key>[A-Za-z0-9_-]+)$")]
    private static partial Regex OldFolder();
}

/// <summary>A link to a Mega folder, as a link to each file in it, its subfolders' too.</summary>
internal sealed class MegaFolderLister(MegaApi api) : IFileLister
{
    public async Task<FileList?> TryListAsync(Uri link, RequestDetails details, CancellationToken cancel)
    {
        if (!MegaSite.IsMega(link) || !MegaSite.TryReadFolder(link, out var folder, out var key))
            return null;
        var nodes = await Retry.RunAsync(Stage.Resolve, LinkProbe.AskRetry, tryCancel => api.FolderAsync(folder, key, tryCancel), cancel);
        var files = nodes.Where(node => node.IsFile).Select(node => $"https://mega.nz/folder/{folder}#{Base64Url.EncodeToString(key)}/file/{node.Handle}").ToList();
        return files.Count > 0
            ? new FileList(nodes.FirstOrDefault(node => !node.IsFile)?.Name, files)
            : throw new StageException(Stage.Link, "This Mega folder has no files that can be read.");
    }
}

/// <summary>
/// One connection from where the part ends, decrypting as the bytes arrive. Mega's check on the whole file is made at the
/// end, so a wrong key or a transfer corrupted on the way fails instead of leaving a file of noise. One connection, as the
/// engine's way of splitting a file isn't open to a plugin; the counter could start anywhere.
/// </summary>
internal sealed class MegaTransport(HttpFetcher http, string userAgent, Uri url, byte[] rawKey, long size) : IFileTransport
{
    private const int BufferBytes = 1 << 16;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);

    public async Task TransferAsync(TransportJob job, CancellationToken cancel)
    {
        await using var part = new FileStream(job.PartPath, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Options = FileOptions.Asynchronous, BufferSize = BufferBytes });
        using var cipher = new MegaCipher(rawKey);
        var buffer = new byte[BufferBytes];
        if (part.Length > size)
            Empty(part); // more than the file: not this file's bytes
        await CountExistingAsync(part, cipher, buffer, cancel);
        if (cipher.Position < size)
            await ReceiveAsync(part, cipher, buffer, job, cancel);
        if (cipher.Matches())
            return;
        Empty(part);
        throw new StageException(Stage.Download, "The file isn't what Mega's key says it should be: the key in the link may be wrong, or the transfer was corrupted on the way.");
    }

    private static void Empty(FileStream part)
    {
        part.SetLength(0);
        part.Position = 0;
    }

    /// <summary>What an earlier try left in the part was decrypted then, and counts towards the check now; the file goes on from its end.</summary>
    private static async Task CountExistingAsync(FileStream part, MegaCipher cipher, byte[] buffer, CancellationToken cancel)
    {
        part.Position = 0;
        int read;
        while ((read = await part.ReadAsync(buffer, cancel)) > 0)
            cipher.Count(buffer.AsSpan(0, read));
    }

    private async Task ReceiveAsync(FileStream part, MegaCipher cipher, byte[] buffer, TransportJob job, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Range = new RangeHeaderValue(cipher.Position, size - 1);
        using var response = await http.SendAsync(request, Stage.Download, cancel, asFile: true);
        if (cipher.Position > 0 && !Continues(response, cipher.Position))
        {
            // the whole file instead of the rest: what is there is replaced by it, as the engine does without ranges
            Empty(part);
            cipher.Restart();
        }
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        while (cipher.Position < size)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            idle.CancelAfter(IdleLimit);
            int read;
            try
            {
                read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - cipher.Position)), idle.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new StageException(Stage.Download, "Mega stopped sending.", isRetryable: true);
            }
            catch (IOException e)
            {
                throw new StageException(Stage.Download, $"The connection dropped: {e.Message}", isRetryable: true, inner: e);
            }
            if (read == 0)
                throw new StageException(Stage.Download, "The connection closed before the file was complete.", isRetryable: true);
            cipher.Decrypt(buffer.AsSpan(0, read));
            await part.WriteAsync(buffer.AsMemory(0, read), cancel);
            job.OnBytes(cipher.Position);
            await job.Pace(read, cancel);
        }
    }

    /// <summary>Whether the answer carries on from <paramref name="offset"/>, as asked.</summary>
    /// <exception cref="StageException">it carries on from somewhere else, which no retry would mend</exception>
    private static bool Continues(HttpResponseMessage response, long offset)
    {
        if (response.StatusCode != HttpStatusCode.PartialContent)
            return false;
        if (response.Content.Headers.ContentRange?.From is not { } from || from != offset)
            throw new StageException(Stage.Download, $"Mega sent the file from byte {response.Content.Headers.ContentRange?.From} where byte {offset} was asked for.");
        return true;
    }
}
