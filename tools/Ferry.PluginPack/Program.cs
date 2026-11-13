using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Ferry.Core.Plugins;
using Ferry.Plugins.Host;

// ferry-pack: a built plugin, as the .ferryplugin people install.
//   ferry-pack <plugin.dll> [--out <folder>] [--key <key.pem>] [--update <https link>]
//   ferry-pack --new-key <key.pem>
//   ferry-pack --check <file.ferryplugin>
// The manifest is written from the plugin itself, so the two can't disagree.

const string Usage = "ferry-pack <plugin.dll> [--out <folder>] [--key <key.pem>] [--update <https link>]\nferry-pack --new-key <key.pem>\nferry-pack --check <file.ferryplugin>";

try
{
    var options = Options(args, out var assemblyPath);
    if (options.TryGetValue("--new-key", out var newKey))
        return NewKey(newKey);
    if (options.TryGetValue("--check", out var toCheck))
        return Say(toCheck);
    if (assemblyPath is null)
        return Fail(Usage);

    var (plugin, type) = PluginLoader.Discover(assemblyPath);
    var update = options.TryGetValue("--update", out var link) ? new Uri(link, UriKind.Absolute) : null;
    var manifest = PluginManifest.From(plugin, Version(assemblyPath), Path.GetFileName(assemblyPath), type, update);
    var folder = Directory.CreateDirectory(options.GetValueOrDefault("--out", Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!)).FullName;
    var package = Path.Combine(folder, $"{manifest.Id}-{manifest.Version}{PluginPackage.Extension}");
    File.Delete(package);
    using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(zip.CreateEntry(PluginManifest.FileName).Open()))
            writer.Write(manifest.ToJson());
        foreach (var file in OwnAssemblies(assemblyPath))
            zip.CreateEntryFromFile(file, "lib/" + Path.GetFileName(file));
    }
    if (options.TryGetValue("--key", out var keyFile))
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyFile));
        PluginPackage.Sign(package, key);
    }

    // read as an install would read it: a package that Ferry would refuse isn't left looking finished
    return Say(package);
}
catch (Exception e) when (e is PluginException or IOException or UnauthorizedAccessException or UriFormatException or CryptographicException or ArgumentException)
{
    return Fail(e.Message);
}

/// <summary>What Ferry makes of a package, as an install would read it.</summary>
static int Say(string package)
{
    var read = PluginPackage.Read(package);
    Console.WriteLine(package);
    Console.WriteLine($"  {read.Manifest.Name} {read.Manifest.Version} ({read.Manifest.Id}), by {read.Manifest.Publisher}");
    Console.WriteLine($"  sha256 {read.Sha256}");
    Console.WriteLine(read.SignedBy is { } signer ? $"  signed by {signer}" : "  not signed");
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static Dictionary<string, string> Options(string[] args, out string? assemblyPath)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    assemblyPath = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
            assemblyPath = args[i];
        else if (i + 1 < args.Length)
            options[args[i]] = args[++i];
        else
            throw new ArgumentException($"{args[i]} needs a value.\n{Usage}");
    }
    return options;
}

static int NewKey(string path)
{
    if (File.Exists(path))
        return Fail($"{path} is there already. A key that is replaced can't sign for the plugins it signed before.");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
    Console.WriteLine($"{path}: keep it to yourself.");
    Console.WriteLine($"  its plugins are signed by {Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))}");
    return 0;
}

/// <summary>The plugin's version as its build says it, without a commit after "+".</summary>
static string Version(string assemblyPath)
{
    var name = AssemblyName.GetAssemblyName(assemblyPath);
    return name.Version?.ToString(3) ?? "0.0.0";
}

/// <summary>The plugin's assembly and what it brought along, without what Ferry has itself.</summary>
static IEnumerable<string> OwnAssemblies(string assemblyPath) =>
    Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!, "*.dll")
             .Where(file => !File.Exists(Path.Combine(AppContext.BaseDirectory, Path.GetFileName(file))));
