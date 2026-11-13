using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ferry.Core.Browse;
using Ferry.Core.Http;
using Ferry.Core.Plugins;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>Plugins people install: the manifest, the package, what the PC keeps of one, and where a package comes from.</summary>
public sealed class PluginTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ferry_plugins_").FullName;
    private readonly FakeSite _github = new();
    private readonly HttpFetcher _http = new();

    public void Dispose()
    {
        _http.Dispose();
        _github.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>A plugin as its author writes it, never loaded here: a manifest is made from it as the pack tool does.</summary>
    private sealed class Example : IBrowsingPlugin
    {
        public static readonly SettingKey Enabled = new("FERRY_PLUGIN_EXAMPLE", SettingType.Boolean, "false", "An example.") { Title = "Example" };
        public static readonly SettingKey Hosts = new("FERRY_EXAMPLE_HOSTS", SettingType.Hosts, "example.test", "Its hosts.");

        public string Id => "tests.example";

        public string Publisher => "Tests";

        public LinkClaims Claims { get; } = new(["fixed.test"], ["example"], Hosts.Name);

        public PagePatterns Pages { get; } = new(@"^/a/(?<slug>[a-z0-9]+)/?$", @"^/f/(?<slug>[a-z0-9]+)/?$");

        public SitePlugin Plugin { get; } = new("Example", Enabled)
        {
            SiteIds = ["example"],
            Options = [Hosts],
            Secrets = [new PluginSecret("example-login", "Login", "Its login.")],
        };

        public ISiteBrowsing Browse(PluginContext context) => throw new NotSupportedException();
    }

    private static PluginManifest Manifest() => PluginManifest.From(new Example(), "1.2.0", "Example.dll", "Tests.Example");

    private string Pack(PluginManifest? manifest = null, string name = "example.ferryplugin", Action<ZipArchive>? more = null, string? manifestJson = null)
    {
        var file = Path.Combine(_root, name);
        File.Delete(file);
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        Write(zip, PluginManifest.FileName, Encoding.UTF8.GetBytes(manifestJson ?? (manifest ?? Manifest()).ToJson()));
        Write(zip, "lib/Example.dll", "not really code"u8.ToArray());
        more?.Invoke(zip);
        return file;
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(bytes);
    }

    [Fact]
    public void ManifestCarriesWhatThePcNeedsAndReadsBackTheSame()
    {
        var manifest = Manifest();

        Assert.True(PluginManifest.TryRead(manifest.ToJson(), out var read, out var reason), reason);
        Assert.Equal(("tests.example", "Example", "1.2.0", PluginApi.Version), (read.Id, read.Name, read.Version, read.Api));
        Assert.Equal(["example"], read.SiteIds);
        Assert.Equal(manifest.Pages, read.Pages);
        Assert.False(read.LooksUpTitles);
        Assert.Equal("FERRY_PLUGIN_EXAMPLE", read.Enabled.ToKey().Name);
        Assert.Equal(SettingType.Hosts, Assert.Single(read.Settings).ToKey().Type);
        Assert.Equal("example-login", Assert.Single(read.Secrets).Name);
    }

    public static TheoryData<string, string> BadManifests => new()
    {
        { "\"api\": 1", "\"api\": 99" },
        { "\"id\": \"tests.example\"", "\"id\": \"../Evil\"" },
        { "\"entry\": \"Example.dll\"", "\"entry\": \"../Example.dll\"" },
        { "\"version\": \"1.2.0\"", "\"version\": \"soon\"" },
        { "\"name\": \"FERRY_PLUGIN_EXAMPLE\"", "\"name\": \"PATH\"" },
        { "\"example\"\n    ],\n    \"hostsSetting\"", "\"https\"\n    ],\n    \"hostsSetting\"" },
        { "\"hostsSetting\": \"FERRY_EXAMPLE_HOSTS\"", "\"hostsSetting\": \"FERRY_FILES_AT_ONCE\"" },
        { "\"fixed.test\"", "\"not a host\"" },
        { "(?\\u003Cslug\\u003E[a-z0-9]\\u002B)/?$\",\n    \"file\"", "[a-z0-9]\\u002B/?$\",\n    \"file\"" },
        { "\"name\": \"example-login\"", "\"name\": \"Example Login\"" },
    };

    [Theory]
    [MemberData(nameof(BadManifests))]
    public void ManifestThatCouldHarmIsRefusedWithItsReason(string good, string bad)
    {
        var json = Manifest().ToJson().ReplaceLineEndings("\n");
        Assert.Contains(good, json, StringComparison.Ordinal);

        Assert.False(PluginManifest.TryRead(json.Replace(good, bad, StringComparison.Ordinal), out _, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void ManifestWithoutWhatItMustHaveIsRefused()
    {
        Assert.False(PluginManifest.TryRead("""{"id":"tests.example","name":"Example"}""", out _, out var reason));
        Assert.Contains(PluginManifest.FileName, reason, StringComparison.Ordinal);
        Assert.False(PluginManifest.TryRead("not json", out _, out _));
    }

    [Fact]
    public void ClaimsAreByHostAndThoseUnderItByTheSettingAndByScheme()
    {
        var claims = new Example().Claims;
        TestPlugins.Register(Example.Enabled, Example.Hosts); // before the settings are read, as an install does
        var settings = new SettingsStore(new AppPaths(_root, _root), new Dictionary<string, string> { ["FERRY_EXAMPLE_HOSTS"] = "moved.test, other.test" }).Load();

        Assert.True(claims.Claims(new Uri("https://fixed.test/a/x")));
        Assert.True(claims.Claims(new Uri("https://cdn.fixed.test/a/x")));
        Assert.False(claims.Claims(new Uri("https://notfixed.test/a/x")));
        Assert.False(claims.Claims(new Uri("https://moved.test/a/x")));
        Assert.True(claims.Claims(new Uri("https://moved.test/a/x"), settings));
        Assert.True(claims.Claims(new Uri("https://OTHER.test/a/x"), settings));
        Assert.True(claims.Claims(new Uri("example:thing")));
        Assert.False(claims.Claims(new Uri("magnet:?xt=1")));
    }

    [Fact]
    public void PagesAreToldApartByTheirPatterns()
    {
        var pages = new Example().Pages;

        Assert.True(pages.TryRead(new Uri("https://fixed.test/a/abc1/"), out var album));
        Assert.Equal((PageKind.Album, "abc1"), (album.Kind, album.Slug));
        Assert.True(pages.TryRead(new Uri("https://fixed.test/f/zz"), out var file));
        Assert.Equal(PageKind.File, file.Kind);
        Assert.False(pages.TryRead(new Uri("https://fixed.test/about"), out _));
        Assert.Null(pages.Problem());
        Assert.NotNull(new PagePatterns(@"^/a/(?=x)(?<slug>.+)$", "^/f/(?<slug>.+)$").Problem()); // a lookahead could take forever
        Assert.NotNull(new PagePatterns("^/a/(.+)$", "^/f/(?<slug>.+)$").Problem());
    }

    [Fact]
    public void PackageIsReadCheckedAndUnpacked()
    {
        var file = Pack();

        var package = PluginPackage.Read(file);
        var unpacked = PluginPackage.Unpack(file, Path.Combine(_root, "unpacked"));

        Assert.Equal("tests.example", package.Manifest.Id);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))), package.Sha256);
        Assert.Null(package.SignedBy);
        Assert.Equal(package.Sha256, unpacked.Sha256);
        Assert.Equal(["Example.dll", PluginManifest.FileName], Directory.GetFiles(Path.Combine(_root, "unpacked")).Select(Path.GetFileName).Order());
        Assert.False(Directory.Exists(Path.Combine(_root, "unpacked.unpacking")));
    }

    [Theory]
    [InlineData("lib/../../evil.dll")]
    [InlineData("lib/sub/inner.dll")]
    [InlineData("readme.txt")]
    [InlineData("/rooted.dll")]
    public void PackageWithAFileOutOfPlaceIsRefused(string name)
    {
        var file = Pack(more: zip => Write(zip, name, [1]));

        var refused = Assert.Throws<PluginException>(() => PluginPackage.Read(file));

        Assert.Contains("no place in a plugin", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageWithoutItsManifestOrItsCodeOrThatIsNoZipIsRefused()
    {
        var noCode = Pack(Manifest() with { Entry = "Other.dll" });
        Assert.Contains("Other.dll", Assert.Throws<PluginException>(() => PluginPackage.Read(noCode)).Message, StringComparison.Ordinal);

        var notZip = Path.Combine(_root, "text.ferryplugin");
        File.WriteAllText(notZip, "hello");
        Assert.Throws<PluginException>(() => PluginPackage.Read(notZip));

        var noManifest = Path.Combine(_root, "bare.ferryplugin");
        using (var zip = ZipFile.Open(noManifest, ZipArchiveMode.Create))
            Write(zip, "lib/Example.dll", [1]);
        Assert.Contains(PluginManifest.FileName, Assert.Throws<PluginException>(() => PluginPackage.Read(noManifest)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SignatureSaysWhoseItIsAndFailsOnceAFileChanges()
    {
        var file = Pack();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PluginPackage.Sign(file, key);

        var signed = PluginPackage.Read(file);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo())), signed.SignedBy);

        // signed again by another key: the first signature is replaced, not kept beside it
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PluginPackage.Sign(file, other);
        Assert.NotEqual(signed.SignedBy, PluginPackage.Read(file).SignedBy);

        using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
        {
            zip.GetEntry("lib/Example.dll")!.Delete();
            Write(zip, "lib/Example.dll", "other code"u8.ToArray());
        }
        Assert.Contains("signature", Assert.Throws<PluginException>(() => PluginPackage.Read(file)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreKeepsOnePackageOfAPluginAndSaysWhatItCantRead()
    {
        var store = new PluginStore(Path.Combine(_root, "store"));
        Assert.Empty(store.List(out _));

        var first = store.Add(PluginPackage.Read(Pack()), Path.Combine(_root, "example.ferryplugin"));
        var newer = Pack(Manifest() with { Version = "1.3.0" }, "newer.ferryplugin");
        store.Add(PluginPackage.Read(newer), newer);
        File.WriteAllText(Path.Combine(_root, "store", "junk.ferryplugin"), "junk");

        var kept = Assert.Single(store.List(out var problems));
        Assert.Equal("1.3.0", kept.Manifest.Version);
        Assert.Equal("tests.example.ferryplugin", kept.ServerName);
        Assert.Equal(first.File, kept.File);
        Assert.Contains("junk.ferryplugin", Assert.Single(problems), StringComparison.Ordinal);

        store.Remove(kept);
        Assert.Empty(store.List(out _));
    }

    [Fact]
    public async Task OnThePcAnInstalledPluginClaimsItsLinksAndDownloadsNone()
    {
        var plugin = ServerOnlyPlugin.For(Manifest());
        TestPlugins.Register([plugin.Enabled, .. plugin.Options]);
        var settings = new SettingsStore(new AppPaths(_root, _root), new Dictionary<string, string> { ["FERRY_PLUGIN_EXAMPLE"] = "true" }).Load();
        var sites = SiteCatalog.Standard(_http, "agent", plugin.Entries(new PluginContext(_http, settings, null)));

        Assert.True(plugin.Owns("example"));
        Assert.True(sites.TryParse("https://example.test/f/abc", out var file, out _)); // by its setting's default
        Assert.Equal("Example", sites.NameOf(file.SiteId));
        Assert.True(sites.TryParse("https://elsewhere.test/f/abc", out var plain, out _));
        Assert.Equal(DirectSite.SiteId, plain.SiteId);
        var refused = await Assert.ThrowsAsync<StageException>(() => sites.ResolveAsync(file, RequestDetails.None, CancellationToken.None));
        Assert.Contains("runs on your server", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServersListOfPackagesIsReadByName()
    {
        var there = PluginSync.Parse("ab" + new string('0', 62) + "  one.ferryplugin\n" + "CD" + new string('1', 62) + " *two.ferryplugin\nsha256sum: nothing here\n");

        Assert.Equal(2, there.Count);
        Assert.Equal("ab" + new string('0', 62), there["one.ferryplugin"]);
        Assert.Equal("cd" + new string('1', 62), there["two.ferryplugin"]);
    }

    [Fact]
    public void ALinkToAPackageIsHttpsAndAReleaseFileIsKnownByItsParts()
    {
        Assert.True(PluginDownload.TryReadLink(" https://example.test/a.ferryplugin ", out _, out _));
        Assert.False(PluginDownload.TryReadLink("http://example.test/a.ferryplugin", out _, out var reason));
        Assert.Contains("https", reason, StringComparison.Ordinal);
        Assert.False(PluginDownload.TryReadLink("somewhere", out _, out _));

        var file = GitHubReleaseFile.TryRead(new Uri("https://github.com/someone/plugins/releases/download/v1.2.0/a%20b.ferryplugin"));
        Assert.Equal(new GitHubReleaseFile("someone", "plugins", "v1.2.0", "a b.ferryplugin"), file);
        Assert.Null(GitHubReleaseFile.TryRead(new Uri("https://github.com/someone/plugins/releases/tag/v1.2.0")));
        Assert.Null(GitHubReleaseFile.TryRead(new Uri("https://example.test/someone/plugins/releases/download/v1/a.ferryplugin")));
    }

    [Fact]
    public async Task APrivateReleasesFileComesThroughTheApiWithTheToken()
    {
        var package = await File.ReadAllBytesAsync(Pack());
        var asset = new Uri(_github.Url, "repos/someone/plugins/releases/assets/7");
        _github.Static["/repos/someone/plugins/releases/tags/v1"] = ("application/json",
            Encoding.UTF8.GetBytes($$"""{"assets":[{"name":"other.zip","url":"{{asset}}0"},{"name":"example.ferryplugin","url":"{{asset}}"}]}"""));
        _github.Static[asset.AbsolutePath] = ("application/octet-stream", package);
        var download = new PluginDownload(_http, _github.Url);

        var file = await download.FetchAsync(new GitHubReleaseFile("someone", "plugins", "v1", "example.ferryplugin"), "secret-token", Path.Combine(_root, "in"), CancellationToken.None);

        Assert.Equal(package, await File.ReadAllBytesAsync(file));
        Assert.Equal("tests.example", PluginPackage.Read(file).Manifest.Id);
        Assert.All(_github.Requests, request => Assert.Equal("Bearer secret-token", request.Headers["Authorization"]));
        Assert.Equal("application/octet-stream", _github.Requests.Last().Headers["Accept"]);
    }

    [Fact]
    public async Task AReleaseTheTokenDoesntOpenOrWithoutTheFileSaysSo()
    {
        var download = new PluginDownload(_http, _github.Url);
        var file = new GitHubReleaseFile("someone", "plugins", "v1", "example.ferryplugin");

        var closed = await Assert.ThrowsAsync<StageException>(() => download.FetchAsync(file, "wrong", Path.Combine(_root, "in"), CancellationToken.None));
        Assert.Contains("token", closed.Message, StringComparison.Ordinal);

        // an answer naming another host for the file is not followed there with the token
        _github.Static["/repos/someone/plugins/releases/tags/v1"] = ("application/json",
            """{"assets":[{"name":"example.ferryplugin","url":"https://elsewhere.test/assets/7"}]}"""u8.ToArray());
        var missing = await Assert.ThrowsAsync<StageException>(() => download.FetchAsync(file, "right", Path.Combine(_root, "in"), CancellationToken.None));
        Assert.Contains("no file called", missing.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.Exists(Path.Combine(_root, "in")) ? Directory.GetFiles(Path.Combine(_root, "in")) : []);
    }

    [Fact]
    public void OnTheAgentASecretIsWhatThePcSent()
    {
        var settings = new SettingsStore(new AppPaths(_root, _root), new Dictionary<string, string>()).Load();
        var secret = new PluginSecret("example-login", "Login", "Its login.");

        var agent = new PluginContext(_http, settings, Secrets: null) { Sent = new Dictionary<string, string> { ["example-login"] = "hunter2" } };

        Assert.True(agent.IsAgent);
        Assert.Equal("hunter2", agent.Secret(secret));
        Assert.Null(agent.Secret(new PluginSecret("another", "Another", "")));
        Assert.Null(new PluginContext(_http, settings, Secrets: null).Secret(secret));
    }
}

/// <summary>Settings of plugins that tests make up, in the one table, as an install puts a plugin's there.</summary>
internal static class TestPlugins
{
    public static void Register(params IEnumerable<SettingKey> keys) => SettingKey.Contribute(keys);
}
