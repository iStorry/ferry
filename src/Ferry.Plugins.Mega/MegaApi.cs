using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Json;

namespace Ferry.Plugins.Mega;

/// <summary>Something in a shared folder.</summary>
/// <param name="RawKey">a file's 32-byte key, already decrypted</param>
internal sealed record MegaNode(string Handle, bool IsFile, string? Name, byte[] RawKey);

/// <summary>What Mega's API is asked, and how to read it: file keys and names are encrypted, and only the link's key opens them.</summary>
internal sealed class MegaApi(HttpFetcher http, string userAgent)
{
    private const string Who = "Mega";
    private const int FileNode = 0;
    private const int FolderNode = 1;

    // a shared folder's whole tree comes in one answer, and some hold tens of thousands of files
    private const long MaxFolderBytes = 32 << 20;

    // its error codes, as its API documentation lists them
    private const int Internal = -1;
    private const int Again = -3;
    private const int RateLimited = -4;
    private const int TooMany = -6;
    private const int NotFound = -9;
    private const int NoAccess = -11;
    private const int WrongKey = -14;
    private const int Blocked = -16;
    private const int OverQuota = -17;
    private const int Unavailable = -18;

    private static readonly Uri Api = new("https://g.api.mega.co.nz/cs");

    /// <summary>Where a file is, its size, and its encrypted attributes.</summary>
    public async Task<(Uri Url, long Size, string Attributes)> FileAsync(string handle, string? inFolder, CancellationToken cancel)
    {
        var request = inFolder is null
            ? $$"""[{"a":"g","g":1,"ssl":2,"p":"{{handle}}"}]"""
            : $$"""[{"a":"g","g":1,"ssl":2,"n":"{{handle}}"}]""";
        return ReadFile(await AskAsync(request, inFolder, HttpAnswers.MaxApiBytes, cancel));
    }

    /// <summary>All that is in a folder, its subfolders included.</summary>
    public async Task<IReadOnlyList<MegaNode>> FolderAsync(string folder, byte[] key, CancellationToken cancel) =>
        ReadFolder(await AskAsync("""[{"a":"f","c":1,"r":1,"ca":1}]""", folder, MaxFolderBytes, cancel), key);

    private async Task<string> AskAsync(string body, string? inFolder, long maxBytes, CancellationToken cancel)
    {
        var number = RandomNumberGenerator.GetInt32(int.MaxValue).ToString(CultureInfo.InvariantCulture);
        var url = inFolder is null ? UrlQuery.With(Api, ("id", number)) : UrlQuery.With(Api, ("id", number), ("n", inFolder));
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.UserAgent.ParseAdd(userAgent);
        using var response = await http.SendAsync(request, Stage.Resolve, cancel);
        return await HttpFetcher.ReadTextAsync(response, maxBytes, Stage.Resolve, cancel);
    }

    /// <exception cref="StageException">Mega refused, or the answer can't be read</exception>
    internal static (Uri Url, long Size, string Attributes) ReadFile(string json)
    {
        using var answer = JsonAnswer.Parse(json, Who);
        var root = First(answer.RootElement);
        if (root.ValueKind == JsonValueKind.Number)
            throw Failure(root, Stage.Resolve);
        try
        {
            if (root.TryGetProperty("g", out var g) && Uri.TryCreate(g.GetString(), UriKind.Absolute, out var url) && root.TryGetProperty("s", out var size) && root.TryGetProperty("at", out var at))
                return (url, size.GetInt64(), at.GetString() ?? "");
        }
        catch (InvalidOperationException e)
        {
            throw CannotRead(Stage.Resolve, e);
        }
        throw new StageException(Stage.Resolve, "Mega didn't say where the file is. It may be over its transfer limit for now.");
    }

    /// <exception cref="StageException">Mega refused, or the answer can't be read</exception>
    internal static IReadOnlyList<MegaNode> ReadFolder(string json, byte[] folderKey)
    {
        using var answer = JsonAnswer.Parse(json, Who, Stage.Link);
        var root = First(answer.RootElement);
        if (root.ValueKind == JsonValueKind.Number)
            throw Failure(root, Stage.Link);
        try
        {
            if (!root.TryGetProperty("f", out var nodes))
                throw new StageException(Stage.Link, "Mega didn't say what is in this folder. Its API may have changed.");
            var read = new List<MegaNode>();
            foreach (var node in nodes.EnumerateArray())
            {
                var kind = node.TryGetProperty("t", out var type) ? type.GetInt32() : -1;
                if (kind is not (FileNode or FolderNode) || !node.TryGetProperty("h", out var handle) || !node.TryGetProperty("k", out var key) || key.GetString() is not { } wrapped)
                    continue;
                var clear = OpenKey(wrapped, folderKey);
                if (clear is null || (kind == FileNode && clear.Length != MegaKeys.RawFileKeyBytes))
                    continue;
                var attributeKey = kind == FileNode ? MegaKeys.Split(clear).Key : clear;
                var name = node.TryGetProperty("a", out var attributes) ? MegaNames.NameOf(attributes.GetString() ?? "", attributeKey) : null;
                read.Add(new MegaNode(handle.GetString() ?? "", kind == FileNode, name, clear));
            }
            return read;
        }
        catch (InvalidOperationException e)
        {
            throw CannotRead(Stage.Link, e);
        }
    }

    /// <summary>What Mega means by the error code it answered with instead.</summary>
    private static StageException Failure(JsonElement code, Stage stage) => (code.TryGetInt32(out var number) ? number : 0) switch
    {
        Internal or Again or Unavailable => new StageException(stage, "Mega is busy, and asks to try again in a moment.", isRetryable: true),
        RateLimited or TooMany => new StageException(stage, "Mega is limiting how often it is asked; trying again more slowly.", isRetryable: true),
        NotFound => new StageException(stage, "Mega has no such file or folder. It may have been removed, or the link may be wrong."),
        NoAccess => new StageException(stage, "Mega won't give access to this. It may be shared with a login only."),
        WrongKey => new StageException(stage, "Mega says the key in this link is wrong."),
        Blocked => new StageException(stage, "Mega has blocked this file or folder."),
        OverQuota => new StageException(stage, "Mega's free transfer quota for this file is used up for now. Try again later."),
        var other => new StageException(stage, $"Mega answered with error {other}."),
    };

    private static StageException CannotRead(Stage stage, Exception inner) => new(stage, "Mega's answer could not be read.", inner: inner);

    /// <summary>A node's key, which the folder's key encrypts, after "owner:".</summary>
    private static byte[]? OpenKey(string wrapped, byte[] folderKey)
    {
        try
        {
            var encrypted = Base64Url.DecodeFromChars(wrapped.AsSpan(wrapped.LastIndexOf(':') + 1));
            if (encrypted.Length % MegaKeys.AesKeyBytes != 0)
                return null;
            using var aes = Aes.Create();
            aes.Key = folderKey;
            return aes.DecryptEcb(encrypted, PaddingMode.None);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null; // one node that can't be opened doesn't stop the others
        }
    }

    private static JsonElement First(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Array && answer.GetArrayLength() > 0 ? answer[0] : answer;
}

/// <summary>Names, as Mega keeps them in a node's encrypted attributes.</summary>
internal static class MegaNames
{
    /// <summary>A file's or folder's name, from its encrypted attributes: "MEGA{json}".</summary>
    public static string? NameOf(string attributes, byte[] key)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            var clear = aes.DecryptCbc(Base64Url.DecodeFromChars(attributes), new byte[MegaKeys.AesKeyBytes], PaddingMode.Zeros);
            var text = Encoding.UTF8.GetString(clear).TrimEnd('\0');
            if (!text.StartsWith("MEGA{", StringComparison.Ordinal))
                return null;
            using var json = JsonDocument.Parse(text.AsMemory(4));
            return json.RootElement.TryGetProperty("n", out var name) ? name.GetString() : null;
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException)
        {
            return null; // the link's own name is good enough
        }
    }
}
