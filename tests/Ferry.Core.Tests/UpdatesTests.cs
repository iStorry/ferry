using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Updates;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>Releases on a fake GitHub (<see cref="FakeSite"/>'s fixed answers): latest, versions, checked downloads, the agent.</summary>
public sealed class UpdatesTests : IDisposable
{
    private readonly FakeSite _github = new();
    private readonly HttpFetcher _http = new();
    private readonly string _root = Directory.CreateTempSubdirectory("ferry_updates_").FullName;

    public void Dispose()
    {
        _http.Dispose();
        _github.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private Releases Releases => new(_http, _github.Url);

    /// <summary>A release with these files (and a SHA256SUMS.txt listing them, unless told otherwise).</summary>
    private void Publish(string path, string tag, IReadOnlyDictionary<string, byte[]> files, bool sums = true, string? tamper = null)
    {
        var assets = new List<string>();
        var lines = new StringBuilder();
        foreach (var (name, data) in files)
        {
            _github.Static[$"/download/{tag}/{name}"] = ("application/octet-stream", name == tamper ? [.. data, 1] : data);
            assets.Add($$"""{"name":"{{name}}","browser_download_url":"{{_github.Url}}download/{{tag}}/{{name}}","size":{{data.Length}}}""");
            lines.Append(Convert.ToHexStringLower(SHA256.HashData(data))).Append("  ").Append(name).Append('\n');
        }
        if (sums)
        {
            _github.Static[$"/download/{tag}/SHA256SUMS.txt"] = ("text/plain", Encoding.UTF8.GetBytes(lines.ToString()));
            assets.Add($$"""{"name":"SHA256SUMS.txt","browser_download_url":"{{_github.Url}}download/{{tag}}/SHA256SUMS.txt","size":{{lines.Length}}}""");
        }
        var json = $$"""{"tag_name":"{{tag}}","body":"What's new","html_url":"https://github.com/x/releases/{{tag}}","draft":false,"assets":[{{string.Join(',', assets)}}]}""";
        _github.Static[path] = ("application/json", Encoding.UTF8.GetBytes(json));
    }

    private const string Latest = $"/repos/{Releases.Repository}/releases/latest";

    [Fact]
    public async Task LatestReleaseIsReadAndCompared()
    {
        Publish(Latest, "v9.1.0", new Dictionary<string, byte[]> { ["Ferry-9.1.0-setup.exe"] = [1, 2, 3] });

        var release = await Releases.LatestAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(new Version(9, 1, 0), release.Version);
        Assert.Equal("What's new", release.Notes);
        Assert.True(release.IsNewerThan("0.1.0+abc"));
        Assert.False(release.IsNewerThan("9.1.0"));
        Assert.Equal(3, release.Asset("ferry-9.1.0-SETUP.exe")!.Size);
    }

    [Fact]
    public async Task NoReleaseYetIsNull() =>
        Assert.Null(await Releases.LatestAsync(CancellationToken.None)); // the fake answers 404

    [Fact]
    public async Task FilesWithoutANameOrAddressAreLeftOutAndABadPageFallsBack()
    {
        const string json = """
            {"tag_name":"v9.1.0","body":"","html_url":"not a url","draft":false,"assets":[
              {"name":"setup.exe","browser_download_url":"","size":1},
              {"name":"","browser_download_url":"https://x.test/a","size":1},
              {"name":"SHA256SUMS.txt","browser_download_url":"https://x.test/sums","size":2}]}
            """;
        _github.Static[Latest] = ("application/json", Encoding.UTF8.GetBytes(json));

        var release = await Releases.LatestAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(Releases.ReleasesPage, release.Page);
        Assert.Equal(["SHA256SUMS.txt"], release.Assets.Select(a => a.Name));
    }

    [Fact]
    public async Task DownloadIsCheckedAgainstItsSum()
    {
        var data = Encoding.UTF8.GetBytes("the installer");
        Publish(Latest, "v9.1.0", new Dictionary<string, byte[]> { ["setup.exe"] = data });
        var release = (await Releases.LatestAsync(CancellationToken.None))!;

        var path = await Releases.DownloadAsync(release, release.Asset("setup.exe")!, _root, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FileThatDoesntMatchIsDeleted()
    {
        Publish(Latest, "v9.1.0", new Dictionary<string, byte[]> { ["setup.exe"] = [1, 2, 3] }, tamper: "setup.exe");
        var release = (await Releases.LatestAsync(CancellationToken.None))!;

        var failure = await Assert.ThrowsAsync<StageException>(() =>
            Releases.DownloadAsync(release, release.Asset("setup.exe")!, _root, CancellationToken.None));

        Assert.Equal(Stage.Update, failure.Stage);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task ReleaseWithoutSumsCantBeUsed()
    {
        Publish(Latest, "v9.1.0", new Dictionary<string, byte[]> { ["setup.exe"] = [1] }, sums: false);
        var release = (await Releases.LatestAsync(CancellationToken.None))!;

        await Assert.ThrowsAsync<StageException>(() => Releases.DownloadAsync(release, release.Asset("setup.exe")!, _root, CancellationToken.None));
    }

    [Theory]
    [InlineData("v0.2.0", 0, 2, 0)]
    [InlineData("0.2", 0, 2, 0)]
    [InlineData("1.4.7+56c8667", 1, 4, 7)]
    [InlineData("2.0.0-beta.1", 2, 0, 0)]
    public void VersionsAreRead(string text, int major, int minor, int patch)
    {
        Assert.True(Releases.TryParseVersion(text, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Fact]
    public void ChecksumLinesAreRead()
    {
        var hash = new string('a', 64);

        var sums = Releases.ParseChecksums($"{hash}  one.zip\n{hash.ToUpperInvariant()} *two.tar.gz\nnot a line\n");

        Assert.Equal(hash, sums["one.zip"]);
        Assert.Equal(hash, sums["two.tar.gz"]);
        Assert.Equal(2, sums.Count);
    }

    [Fact]
    public async Task AgentComesFromThisVersionsReleaseOnce()
    {
        var program = Encoding.UTF8.GetBytes("#!/agent");
        var archive = new MemoryStream();
        await using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        await using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "ferry") { DataStream = new MemoryStream(program) };
            await tar.WriteEntryAsync(entry);
        }
        var name = $"ferry-{Product.Version}-linux-x64.tar.gz";
        Publish($"/repos/{Releases.Repository}/releases/tags/v{Product.Version}", $"v{Product.Version}",
                new Dictionary<string, byte[]> { [name] = archive.ToArray() });

        var path = await AgentDownloads.FetchAsync(Releases, "linux-x64", _root, CancellationToken.None);
        var requests = _github.Requests.Count;
        var again = await AgentDownloads.FetchAsync(Releases, "linux-x64", _root, CancellationToken.None);

        Assert.Equal(program, await File.ReadAllBytesAsync(path!));
        Assert.Equal(path, again);
        Assert.Equal(requests, _github.Requests.Count); // cached
        Assert.Equal([Path.GetFileName(path!)], Directory.GetFiles(_root).Select(Path.GetFileName)); // the archive is gone
    }

    [Fact]
    public async Task NoReleaseMeansNoAgent() =>
        Assert.Null(await AgentDownloads.FetchAsync(Releases, "linux-x64", _root, CancellationToken.None));
}
