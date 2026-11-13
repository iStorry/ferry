using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ferry.Core.Plugins;

/// <summary>"plugin.sig" in a package: who signed it, and their signature over its files.</summary>
/// <param name="Key">the signer's public key (ECDSA P-256, SubjectPublicKeyInfo, base64)</param>
/// <param name="Signature">over the list of the package's other files with their SHA-256, base64</param>
public sealed record PluginSignature(string Key, string Signature);

/// <summary>
/// A plugin as a file, "name.ferryplugin": a zip holding <see cref="PluginManifest.FileName"/>, its code in "lib/", and
/// perhaps a signature. Reading one runs nothing of it and trusts nothing it says about its own size.
/// </summary>
/// <param name="Sha256">of the file itself, lower-case hex: what tells one build of a plugin from another</param>
/// <param name="SignedBy">the SHA-256 of the key whose signature matches its files, lower-case hex; null when it isn't signed</param>
public sealed record PluginPackage(PluginManifest Manifest, string Sha256, string? SignedBy)
{
    public const string Extension = ".ferryplugin";
    public const string SignatureName = "plugin.sig";
    private const string LibFolder = "lib/";

    private const long MaxPackedBytes = 64L << 20;
    private const long MaxUnpackedBytes = 256L << 20;
    private const int MaxFiles = 500;
    private const int MaxTextBytes = 1 << 20;

    /// <summary>Check <paramref name="file"/> and say what it is.</summary>
    /// <exception cref="PluginException">it isn't a package Ferry can use; the message says why</exception>
    public static PluginPackage Read(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MaxPackedBytes)
                throw new PluginException($"It is larger than {MaxPackedBytes >> 20} MB, the most a plugin may be.");
            string sha256;
            using (var stream = File.OpenRead(file))
                sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            using var zip = ZipFile.OpenRead(file);
            var entries = Entries(zip);
            if (!entries.TryGetValue(PluginManifest.FileName, out var manifestEntry))
                throw new PluginException($"It has no {PluginManifest.FileName}, so it isn't a Ferry plugin.");
            if (!PluginManifest.TryRead(Encoding.UTF8.GetString(ReadAll(manifestEntry, MaxTextBytes)), out var manifest, out var reason))
                throw new PluginException(reason);
            if (!entries.ContainsKey(LibFolder + manifest.Entry))
                throw new PluginException($"Its code, {manifest.Entry}, isn't in it.");
            return new PluginPackage(manifest, sha256, Signer(entries));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new PluginException($"It can't be read as a plugin: {e.Message}", e);
        }
    }

    /// <summary>Check <paramref name="file"/>, then put its manifest and code in <paramref name="folder"/>, which must not be there yet.</summary>
    /// <exception cref="PluginException">it isn't a package Ferry can use, or it can't be written there</exception>
    public static PluginPackage Unpack(string file, string folder)
    {
        var package = Read(file);
        var scratch = folder + ".unpacking";
        try
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
            var root = Directory.CreateDirectory(scratch).FullName + Path.DirectorySeparatorChar;
            using var zip = ZipFile.OpenRead(file);
            var left = MaxUnpackedBytes;
            foreach (var (name, entry) in Entries(zip).Where(entry => entry.Key != SignatureName))
            {
                // Entries let only plain names through; this is the second lock on the same door
                var target = Path.GetFullPath(Path.Combine(root, Path.GetFileName(name)));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    throw new PluginException($"\"{name}\" would be written outside the plugin's folder.");
                using var from = entry.Open();
                using var to = File.Create(target);
                left -= Copy(from, to.Write, left);
            }
            Directory.Move(scratch, folder);
            return package;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new PluginException($"It couldn't be unpacked: {e.Message}", e);
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>Sign <paramref name="file"/> with <paramref name="key"/>, in place of any signature it has.</summary>
    public static void Sign(string file, ECDsa key)
    {
        using var zip = ZipFile.Open(file, ZipArchiveMode.Update);
        zip.GetEntry(SignatureName)?.Delete();
        var signature = new PluginSignature(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                                            Convert.ToBase64String(key.SignData(Listing(Entries(zip)), HashAlgorithmName.SHA256)));
        using var writer = new StreamWriter(zip.CreateEntry(SignatureName).Open());
        writer.Write(JsonSerializer.Serialize(signature, PluginJson.Default.PluginSignature));
    }

    /// <summary>The package's files by name, each allowed to be there: the manifest, the signature, and files right in "lib/".</summary>
    private static SortedDictionary<string, ZipArchiveEntry> Entries(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxFiles)
            throw new PluginException($"It has more than {MaxFiles} files.");
        var entries = new SortedDictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries.Where(entry => entry.FullName != LibFolder))
        {
            var name = entry.FullName;
            var plain = name is PluginManifest.FileName or SignatureName
                        || (name.StartsWith(LibFolder, StringComparison.Ordinal) && IsFileName(name[LibFolder.Length..]));
            if (!plain || !entries.TryAdd(name, entry))
                throw new PluginException($"\"{name}\" has no place in a plugin: it holds {PluginManifest.FileName} and files in {LibFolder}, each once.");
        }
        if (entries.Values.Sum(entry => entry.Length) > MaxUnpackedBytes)
            throw new PluginException($"Unpacked it is larger than {MaxUnpackedBytes >> 20} MB.");
        return entries;
    }

    private static bool IsFileName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\', ':']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>Whose signature matches these files; null when there is none.</summary>
    /// <exception cref="PluginException">it has a signature that doesn't match: the files were changed after signing</exception>
    private static string? Signer(SortedDictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.TryGetValue(SignatureName, out var entry))
            return null;
        try
        {
            var signature = JsonSerializer.Deserialize(ReadAll(entry, MaxTextBytes), PluginJson.Default.PluginSignature)
                            ?? throw new PluginException("Its signature is empty.");
            var key = Convert.FromBase64String(signature.Key);
            using var signer = ECDsa.Create();
            signer.ImportSubjectPublicKeyInfo(key, out _);
            return signer.VerifyData(Listing(entries), Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256)
                ? Convert.ToHexStringLower(SHA256.HashData(key))
                : throw new PluginException("Its signature doesn't match its files: it was changed after it was signed.");
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException)
        {
            throw new PluginException("Its signature can't be read.", e);
        }
    }

    /// <summary>What a signature is over: every file but the signature, in order of name, as "{sha256}  {name}" lines.</summary>
    private static byte[] Listing(SortedDictionary<string, ZipArchiveEntry> entries)
    {
        var listing = new StringBuilder();
        var left = MaxUnpackedBytes;
        foreach (var (name, entry) in entries.Where(entry => entry.Key != SignatureName))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = entry.Open();
            left -= Copy(stream, hash.AppendData, left);
            listing.Append(Convert.ToHexStringLower(hash.GetHashAndReset())).Append("  ").Append(name).Append('\n');
        }
        return Encoding.UTF8.GetBytes(listing.ToString());
    }

    private static byte[] ReadAll(ZipArchiveEntry entry, int most)
    {
        using var stream = entry.Open();
        using var bytes = new MemoryStream();
        Copy(stream, bytes.Write, most);
        return bytes.ToArray();
    }

    private delegate void Sink(ReadOnlySpan<byte> bytes);

    /// <summary>Copy all of <paramref name="from"/>, as long as it is no more than <paramref name="most"/>: a zip can say less than it holds.</summary>
    /// <returns>how many bytes there were</returns>
    private static long Copy(Stream from, Sink to, long most)
    {
        Span<byte> buffer = stackalloc byte[8192];
        long copied = 0;
        int read;
        while ((read = from.Read(buffer)) > 0)
        {
            copied += read;
            if (copied > most)
                throw new PluginException("It unpacks to more than a plugin may be.");
            to(buffer[..read]);
        }
        return copied;
    }
}
