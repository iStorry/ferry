using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Ferry.Core.Settings;

namespace Ferry.Core.Files;

/// <summary>
/// Secrets at rest, one file each: on Windows encrypted for the current user (DPAPI); elsewhere a file only the user
/// can read (0600, in a 0700 folder). Never in settings.json, logs or output.
/// </summary>
public sealed class SecretStore(string folder)
{
    private const string Suffix = ".secret";

    public static SecretStore For(AppPaths paths) => new(Path.Combine(paths.ConfigFolder, "secrets"));

    /// <summary>The folder the secrets are in.</summary>
    public string Folder => folder;

    /// <exception cref="IOException">the secret can't be written</exception>
    public void Save(string name, byte[] secret)
    {
        CreatePrivateFolder();
        var path = PathOf(name);
        var bytes = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(secret, Entropy(name), DataProtectionScope.CurrentUser)
            : secret;
        // DPAPI keeps the bytes for the user; elsewhere only the file's mode does
        AtomicFile.Write(path, bytes, OperatingSystem.IsWindows() ? null : PrivateFiles.CreatePrivate);
    }

    /// <summary>The secret, or null if there is none (or it can't be decrypted: it was saved by another user).</summary>
    public byte[]? Load(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
            return null;
        var bytes = File.ReadAllBytes(path);
        if (!OperatingSystem.IsWindows())
            return bytes;
        try
        {
            return ProtectedData.Unprotect(bytes, Entropy(name), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>A file is saved under this name, whether or not it can be decrypted.</summary>
    public bool Has(string name) => File.Exists(PathOf(name));

    /// <summary>Remove the secret; one that was never saved (on a new install, not even its folder) is fine.</summary>
    public void Delete(string name)
    {
        if (File.Exists(PathOf(name))) // deleting in a folder that doesn't exist throws
            File.Delete(PathOf(name));
    }

    /// <summary>The file a secret is saved in, for telling the user where it is.</summary>
    public string PathOf(string name) => Path.Combine(folder, name + Suffix);

    // ties the encrypted bytes to their name, so one secret's file can't be passed off as another's
    private static byte[] Entropy(string name) => Encoding.UTF8.GetBytes($"{Product.Name}:{name}");

    private void CreatePrivateFolder()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(folder);
        else
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
