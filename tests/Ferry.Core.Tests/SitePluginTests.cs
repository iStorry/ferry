using Ferry.Core;
using Ferry.Core.Download;
using Ferry.Core.Http;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.AfterDownload;
using Ferry.Plugins.CloudLinks;
using Ferry.Plugins.Debrid;
using Ferry.Plugins.DevReleases;
using Ferry.Plugins.Feeds;
using Ferry.Plugins.FileHosts;
using Ferry.Plugins.FileServers;
using Ferry.Plugins.Folders;
using Ferry.Plugins.Mega;
using Ferry.Plugins.OpenDirectories;
using Ferry.Plugins.RemoteQueues;
using Ferry.Plugins.Torrents;
using Ferry.Plugins.VideoStreams;
using Xunit;

namespace Ferry.Core.Tests;

/// <summary>Which links the plugins take, what they leave to the plain site, and how they read what sites answer. Nothing here contacts a real service.</summary>
public sealed class SitePluginTests : IDisposable
{
    private const string UserAgent = "test";

    private readonly HttpFetcher _http = new();

    public void Dispose() => _http.Dispose();

    private SiteCatalog Catalog() =>
        SiteCatalog.Standard(_http, UserAgent,
        [
            new SiteEntry(new GoogleDriveSite(_http, UserAgent)), new SiteEntry(DropboxSite.Create(_http, UserAgent)), new SiteEntry(new OneDriveSite(_http, UserAgent)),
            new SiteEntry(new GitHubSite(_http, UserAgent, TargetSystem.Windows)), new SiteEntry(RepoFileSites.GitLab(_http, UserAgent)), new SiteEntry(RepoFileSites.HuggingFace(_http, UserAgent)), new SiteEntry(RepoFileSites.SourceForge(_http, UserAgent)),
            new SiteEntry(PixeldrainSite.Create(_http, UserAgent)), new SiteEntry(new MediaFireSite(_http, UserAgent)), new SiteEntry(new CyberdropSite(_http, UserAgent)),
            new SiteEntry(new GofileSite(UserAgent, new GofileApi(_http, UserAgent))),
            new SiteEntry(new HlsSite(_http, UserAgent)), new SiteEntry(new MegaSite(_http, UserAgent)), new SiteEntry(new FileServerSite(UserAgent)),
        ]);

    [Theory]
    [InlineData("https://drive.google.com/file/d/abc_DEF-1/view?usp=sharing", GoogleDriveSite.SiteId, "abc_DEF-1")]
    [InlineData("https://drive.google.com/open?id=abc_DEF-1", GoogleDriveSite.SiteId, "abc_DEF-1")]
    [InlineData("https://docs.google.com/uc?export=download&id=abc_DEF-1", GoogleDriveSite.SiteId, "abc_DEF-1")]
    [InlineData("https://docs.google.com/document/d/abc/export?format=pdf#x", GoogleDriveSite.SiteId, "https://docs.google.com/document/d/abc/export?format=pdf")]
    [InlineData("https://docs.google.com/presentation/d/abc/export/pdf", GoogleDriveSite.SiteId, "https://docs.google.com/presentation/d/abc/export/pdf")]
    [InlineData("https://cyberdrop.me/f/abc", CyberdropSite.SiteId, "abc")]
    [InlineData("https://www.cyberdrop.cc/f/abc", CyberdropSite.SiteId, "abc")]
    [InlineData("https://www.dropbox.com/scl/fi/x1/a.zip?rlkey=k&dl=0", DropboxSite.SiteId, "/scl/fi/x1/a.zip")]
    [InlineData("https://www.dropbox.com/s/x1/a.zip?dl=0", DropboxSite.SiteId, "/s/x1/a.zip")]
    [InlineData("https://github.com/o/r/releases/latest", GitHubSite.SiteId, "/o/r/releases/latest")]
    [InlineData("https://github.com/o/r/releases/tag/v1.2", GitHubSite.SiteId, "/o/r/releases/tag/v1.2")]
    [InlineData("https://github.com/o/r/blob/main/a/b.txt", GitHubSite.SiteId, "/o/r/blob/main/a/b.txt")]
    [InlineData("https://gitlab.com/g/s/r/-/blob/main/b.txt", RepoFileSites.GitLabId, "/g/s/r/-/blob/main/b.txt")]
    [InlineData("https://huggingface.co/o/m/blob/main/w.bin", RepoFileSites.HuggingFaceId, "/o/m/blob/main/w.bin")]
    [InlineData("https://sourceforge.net/projects/p/files/d/a.zip/download", RepoFileSites.SourceForgeId, "/projects/p/files/d/a.zip/download")]
    [InlineData("https://pixeldrain.com/u/abc123", PixeldrainSite.SiteId, "/u/abc123")]
    [InlineData("https://www.mediafire.com/file/abc123/a.zip/file", MediaFireSite.SiteId, "abc123")]
    [InlineData("https://cdn.example.com/show/master.m3u8?token=1", HlsSite.SiteId, "https://cdn.example.com/show/master.m3u8?token=1")]
    [InlineData("https://mega.nz/file/hAnDle1#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", MegaSite.SiteId, "hAnDle1")]
    [InlineData("ftp://example.com/a/b.zip", FileServerSite.SiteId, "ftp://example.com/a/b.zip")]
    [InlineData("sftp://me@example.com/a/b.zip", FileServerSite.SiteId, "sftp://me@example.com/a/b.zip")]
    public void TakesItsLinks(string text, string site, string key)
    {
        Assert.True(Catalog().TryParse(text, out var file, out var reason), reason);
        Assert.Equal(site, file.SiteId);
        Assert.Equal(key, file.Key);
    }

    [Theory]
    [InlineData("https://github.com/o/r/releases/download/v1/a.zip")]
    [InlineData("https://github.com/o/r/releases/latest/download/a.zip")]
    [InlineData("https://github.com/o/r/archive/refs/heads/main.zip")]
    [InlineData("https://github.com/o/r")]
    [InlineData("https://gitlab.com/g/r/-/raw/main/b.txt")]
    [InlineData("https://huggingface.co/o/m/resolve/main/w.bin")]
    [InlineData("https://sourceforge.net/projects/p/files/d/")]
    [InlineData("https://dl.dropboxusercontent.com/s/x1/a.zip")]
    [InlineData("https://contoso.sharepoint.com/sites/x/doc.pdf")]
    [InlineData("https://pixeldrain.com/api/file/abc123")]
    [InlineData("https://cyberdrop.example.org/f/abc")]
    public void LeavesWhatIsAlreadyAFile(string text)
    {
        Assert.True(Catalog().TryParse(text, out var file, out var reason), reason);
        Assert.Equal(DirectSite.SiteId, file.SiteId);
    }

    [Theory]
    [InlineData("https://drive.google.com/drive/folders/abc")]
    [InlineData("https://docs.google.com/document/d/abc/edit")]
    [InlineData("https://drive.google.com/")]
    [InlineData("https://1drv.ms/f/s!abc")]
    [InlineData("https://contoso.sharepoint.com/:f:/g/personal/x/abc")]
    [InlineData("https://mega.nz/folder/abc#key")]
    [InlineData("https://mega.nz/file/abc")]
    [InlineData("ftp://me:secret@example.com/a.zip")]
    [InlineData("ftp://example.com/folder/")]
    public void RefusesWhatItCannotTake(string text) => Assert.False(Catalog().TryParse(text, out _, out _));

    [Theory]
    [InlineData("https://drive.google.com/drive/folders/abc", "Folders and Albums")]
    [InlineData("https://gofile.io/d/abc", "File Hosts")]
    [InlineData("https://mega.nz/folder/abc#AAAAAAAAAAAAAAAAAAAAAA", "Mega")]
    public void NamesThePluginThatAddsAFolder(string text, string plugin)
    {
        Assert.False(Catalog().TryParse(text, out _, out var reason));
        Assert.Contains($"Folders are added through {plugin}.", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsLinksAndTitlesFromHtml()
    {
        Assert.Equal(["a.zip", "b.zip", "c&d.zip"], HtmlLinks.Hrefs("""<a href=a.zip>x</a> <a class=y href='b.zip'>y</a> <a href="c&amp;d.zip">z</a>"""));
        Assert.Equal("A & B", HtmlLinks.Title("<html><head><TITLE> A &amp; B </TITLE></head></html>"));
        Assert.Null(HtmlLinks.Title("<html><title></title></html>"));
        Assert.Null(HtmlLinks.Title("<html></html>"));
    }

    [Fact]
    public void ASiteThatIsOffStillKnowsItsLinks()
    {
        var catalog = new SiteCatalog([new SiteEntry(new GoogleDriveSite(_http, UserAgent), IsOn: false)]);
        var site = catalog.OffSiteOf("https://drive.google.com/file/d/abc/view");
        Assert.Equal(GoogleDriveSite.SiteId, site?.Id);
    }

    [Theory]
    [InlineData("""<input type="hidden" name="uuid" value="p-1">""", "p-1")]
    [InlineData("""<input value="p-1" name="uuid" type="hidden">""", "p-1")]
    [InlineData("<html>Quota exceeded</html>", null)]
    public void FindsTheWarningPass(string page, string? pass) => Assert.Equal(pass, GoogleDriveSite.WarningPass(page));

    [Fact]
    public void AsksOneDriveAsItsDocumentationSays() =>
        // unpadded base64url of the link, after "u!", as Microsoft's documentation of the shares API says
        Assert.EndsWith("/shares/u!aHR0cHM6Ly9vbmVkcml2ZS5saXZlLmNvbS9yZWRpcj9yZXNpZD0xMjM0/root/content",
                        OneDriveSite.SharesUrl(new Uri("https://onedrive.live.com/redir?resid=1234")).AbsolutePath, StringComparison.Ordinal);

    [Theory]
    [InlineData("tool-1.0-win-x64.zip", "tool-1.0-linux-x64.tar.gz", "tool-1.0-macos-arm64.zip")]
    [InlineData("tool_x86_64-pc-windows-msvc.zip", "tool_aarch64-pc-windows-msvc.zip", "tool_x86_64-unknown-linux-gnu.tar.gz")]
    [InlineData("tool-setup-x64.exe", "tool-setup-x64.exe.sha256", "tool.dmg")]
    [InlineData("tool.zip", "tool.deb", "checksums.txt")]
    public void PicksTheFileForWindows(string expected, string other, string another) =>
        Assert.Equal(expected, ReleaseAssets.Pick([other, expected, another], TargetSystem.Windows));

    [Theory]
    [InlineData("tool-1.0-linux-x64.tar.gz", "tool-1.0-win-x64.zip", "tool-1.0-macos-arm64.zip")]
    [InlineData("tool_x86_64-unknown-linux-gnu.tar.gz", "tool_aarch64-unknown-linux-gnu.tar.gz", "tool_x86_64-pc-windows-msvc.zip")]
    [InlineData("tool.AppImage", "tool-setup-x64.exe", "tool.deb")]
    public void PicksTheFileForLinux(string expected, string other, string another) =>
        Assert.Equal(expected, ReleaseAssets.Pick([other, expected, another], TargetSystem.Linux));

    [Fact]
    public void PicksTheFileForAnArmMachine() =>
        Assert.Equal("tool-macos-arm64.zip", ReleaseAssets.Pick(["tool-macos-x64.zip", "tool-macos-arm64.zip"], TargetSystem.Mac with { Arm = true }));

    [Fact]
    public void AsksWhenTheNamesDoNotSay()
    {
        var e = Assert.Throws<StageException>(() => ReleaseAssets.Pick(["tool-a.zip", "tool-b.zip"], TargetSystem.Windows));
        Assert.Contains("tool-a.zip", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysWhenTheReleaseIsEmptyOrForElsewhere()
    {
        Assert.Throws<StageException>(() => ReleaseAssets.Pick([], TargetSystem.Windows));
        Assert.Throws<StageException>(() => ReleaseAssets.Pick(["tool-linux.tar.gz", "tool.dmg"], TargetSystem.Windows));
    }

    [Fact]
    public void ReadsAGitHubRelease()
    {
        var assets = GitHubSite.Read("""{"assets":[{"id":7,"name":"a.zip","size":5,"browser_download_url":"https://github.com/o/r/releases/download/v1/a.zip"},{"id":8,"name":"","browser_download_url":"https://x.example/"}]}""");
        Assert.Equal([new GitHubSite.Asset(7, "a.zip", 5, new Uri("https://github.com/o/r/releases/download/v1/a.zip"))], assets);
        Assert.Empty(GitHubSite.Read("""{"message":"Not Found"}"""));
        Assert.Throws<StageException>(() => GitHubSite.Read("<html>"));
    }

    [Fact]
    public void ReadsDebridAnswers()
    {
        var (url, name, size) = DebridSite.ReadRealDebrid("""{"download":"https://x.example/f","filename":"a.zip","filesize":5}""");
        Assert.Equal((new Uri("https://x.example/f"), "a.zip", 5L), (url, name, size));
        (url, name, size) = DebridSite.ReadAllDebrid("""{"status":"success","data":{"link":"https://y.example/f","filename":"b.zip","filesize":7}}""");
        Assert.Equal((new Uri("https://y.example/f"), "b.zip", 7L), (url, name, size));
        var e = Assert.Throws<StageException>(() => DebridSite.ReadAllDebrid("""{"status":"error","error":{"code":"X","message":"No such host"}}"""));
        Assert.Contains("No such host", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsMediaFiresButton()
    {
        Assert.Equal(new Uri("https://download1.mediafire.com/a/b.zip"),
                     MediaFireSite.DownloadUrl("""<a class="x" id="downloadButton" href="https://download1.mediafire.com/a/b.zip">Download</a>"""));
        Assert.Null(MediaFireSite.DownloadUrl("<html>gone</html>"));
    }

    [Fact]
    public void ReadsADriveFoldersListing()
    {
        var (files, folders) = DriveFolderLister.Read("""
            <a href="https://drive.google.com/file/d/f1/view?usp=drive_web">a</a>
            <a href="https://drive.google.com/drive/folders/inner">b</a>
            <a href="https://docs.google.com/document/d/d1/edit">c</a>
            """);
        Assert.Equal(["https://drive.google.com/file/d/f1/view?usp=drive_web"], files);
        Assert.Equal(["inner"], folders);
    }

    [Fact]
    public void ReadsAPixeldrainList()
    {
        var list = PixeldrainListLister.Read("""{"title":"Set","files":[{"id":"a1"},{"id":"b2"}]}""", new Uri("https://pixeldrain.com/l/x"));
        Assert.Equal("Set", list.Title);
        Assert.Equal(["https://pixeldrain.com/u/a1", "https://pixeldrain.com/u/b2"], list.Links);
    }

    [Fact]
    public void ReadsADirectoryListing()
    {
        const string Page = """
            <html><head><title>Index of /files/</title></head><body><h1>Index of /files/</h1>
            <a href="?C=N;O=D">Name</a> <a href="../">Parent</a> <a href="/">Root</a>
            <a href="a.zip">a.zip</a> <a href="inner/">inner/</a> <a href="https://elsewhere.example/b.zip">b</a>
            </body></html>
            """;
        Assert.True(DirectoryLister.IsListing(Page));
        Assert.False(DirectoryLister.IsListing("<html><title>Welcome</title></html>"));
        var (files, folders) = DirectoryLister.Read(Page, new Uri("https://example.com/files/"));
        Assert.Equal([new Uri("https://example.com/files/a.zip")], files);
        Assert.Equal([new Uri("https://example.com/files/inner/")], folders);
    }

    [Fact]
    public void ReadsFeeds()
    {
        var rss = FeedReader.Files("""
            <rss version="2.0"><channel>
              <item><title>one</title><link>https://example.com/post</link><enclosure url="https://example.com/1.mp3" /></item>
              <item><title>two</title><link>https://example.com/2.zip</link></item>
              <item><title>three</title><link>https://example.com/post.html</link></item>
            </channel></rss>
            """);
        Assert.Equal([new Uri("https://example.com/1.mp3"), new Uri("https://example.com/2.zip")], rss);
        var atom = FeedReader.Files("""
            <feed xmlns="http://www.w3.org/2005/Atom"><entry>
              <link rel="alternate" href="https://example.com/page" /><link rel="enclosure" href="https://example.com/3.iso" />
            </entry></feed>
            """);
        Assert.Equal([new Uri("https://example.com/3.iso")], atom);
    }

    [Fact]
    public void FeedsWontReadADocumentType() =>
        Assert.Throws<System.Xml.XmlException>(() => FeedReader.Files("""<!DOCTYPE rss [<!ENTITY x "y">]><rss />"""));

    [Fact]
    public void PicksAStreamsBestQuality()
    {
        var playlist = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=900,RESOLUTION=1920x1080\nhigh.m3u8\n", new Uri("https://x.example/a/master.m3u8"));
        Assert.Equal(new Uri("https://x.example/a/high.m3u8"), playlist.Best);
    }

    [Fact]
    public void ReadsAStreamsPieces()
    {
        var playlist = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\"\n#EXTINF:4,\none.ts\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:4,\ntwo.ts\n#EXT-X-ENDLIST\n", new Uri("https://x.example/a/v.m3u8"));
        Assert.False(playlist.IsLive);
        Assert.Equal(2, playlist.Segments.Count);
        Assert.Equal(new Uri("https://x.example/a/k.bin"), playlist.Segments[0].Key);
        Assert.Equal<byte?>(7, playlist.Segments[0].Iv?[15]);
        Assert.Null(playlist.Segments[1].Key);
    }

    [Theory]
    [InlineData("METHOD=SAMPLE-AES,URI=\"skd://x\"")]
    [InlineData("METHOD=AES-128,URI=\"k\",KEYFORMAT=\"com.widevine\"")]
    public void RefusesDrm(string key)
    {
        var e = Assert.Throws<StageException>(() => HlsPlaylist.Parse($"#EXTM3U\n#EXT-X-KEY:{key}\n#EXTINF:4,\none.ts\n#EXT-X-ENDLIST\n", new Uri("https://x.example/v.m3u8")));
        Assert.Contains("DRM", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsADashManifest()
    {
        var manifest = DashManifest.Parse("""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate media="$RepresentationID$/$Number%03d$.m4s" initialization="$RepresentationID$/init.mp4" duration="4" timescale="1" />
                  <Representation id="low" bandwidth="100" />
                  <Representation id="high" bandwidth="900" />
                </AdaptationSet>
                <AdaptationSet contentType="audio">
                  <Representation id="a" bandwidth="64">
                    <SegmentTemplate media="a/$Time$.m4s" initialization="a/init.mp4">
                      <SegmentTimeline><S t="0" d="5" r="1" /></SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """, new Uri("https://x.example/v/m.mpd"));
        Assert.Equal(new Uri("https://x.example/v/high/init.mp4"), manifest.Video.Init);
        Assert.Equal([new Uri("https://x.example/v/high/001.m4s"), new Uri("https://x.example/v/high/002.m4s"), new Uri("https://x.example/v/high/003.m4s")],
                     manifest.Video.Segments.Select(piece => piece.Url));
        Assert.Equal([new Uri("https://x.example/v/a/0.m4s"), new Uri("https://x.example/v/a/5.m4s")], manifest.Audio?.Segments.Select(piece => piece.Url));
    }

    [Theory]
    [InlineData("""<MPD type="dynamic"><Period /></MPD>""", "live")]
    [InlineData("""<MPD><Period><AdaptationSet contentType="video"><ContentProtection /><Representation id="a" bandwidth="1" /></AdaptationSet></Period></MPD>""", "DRM")]
    public void RefusesLiveAndProtectedDash(string xml, string word)
    {
        var e = Assert.Throws<StageException>(() => DashManifest.Parse(xml, new Uri("https://x.example/m.mpd")));
        Assert.Contains(word, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsGofileFolders()
    {
        var (title, files) = GofileApi.Read("""{"status":"ok","data":{"name":"Set","children":{"f1":{"type":"file","name":"a.zip","link":"https://store.example/a.zip","size":5},"d1":{"type":"folder","name":"x"}}}}""");
        Assert.Equal("Set", title);
        Assert.Equal([new GofileEntry("f1", "a.zip", new Uri("https://store.example/a.zip"), 5)], files);
        Assert.Throws<StageException>(() => GofileApi.Read("""{"status":"error-notFound"}"""));
    }

    [Fact]
    public void ReadsCyberdrop()
    {
        var (name, ask) = CyberdropSite.ReadInfo("""{"name":"a.mp4","auth_url":"https://auth.example/x"}""");
        Assert.Equal(("a.mp4", new Uri("https://auth.example/x")), (name, ask));
        Assert.Equal(new Uri("https://c.example/a.mp4"), CyberdropSite.ReadUrl("""{"url":"https://c.example/a.mp4"}"""));
        var album = CyberdropLister.Read("""<h1 id="title">My set</h1><a class="image" href="/f/abc">x</a><a href="https://elsewhere.example/f/no">y</a>""", new Uri("https://cyberdrop.me/a/s1"));
        Assert.Equal("My set", album.Title);
        Assert.Equal(["https://cyberdrop.me/f/abc"], album.Links);
    }

    [Fact]
    public void ApplesPlacementRules()
    {
        // a full path of this machine's kind: a drive letter is no root on Linux
        var isos = Path.GetFullPath("ISOs");
        var moves = PlacementRules.Read($"*.iso={isos}; *.txt=not{Path.DirectorySeparatorChar}full; bad");
        Assert.Equal(isos, PlacementRules.Folder(moves, "a.ISO", "here"));
        Assert.Equal("here", PlacementRules.Folder(moves, "a.txt", "here"));
        Assert.Equal("here", PlacementRules.Folder(moves, "a.zip", "here"));
        var renames = PlacementRules.Read("*.jpeg={name}.jpg; r*=Work {name}{ext}");
        Assert.Equal("a.jpg", PlacementRules.Name(renames, "a.jpeg"));
        Assert.Equal("Work r1.pdf", PlacementRules.Name(renames, "r1.pdf"));
        Assert.Equal("z.pdf", PlacementRules.Name(renames, "z.pdf"));
    }

    [Theory]
    [InlineData("a.part2.rar", true)]
    [InlineData("a.part10.rar", true)]
    [InlineData("a.part1.rar", false)]
    [InlineData("a.part01.rar", false)]
    [InlineData("a.rar", false)]
    public void UnpacksASplitArchiveFromItsFirstVolume(string name, bool later) => Assert.Equal(later, FinishedFileWork.IsLaterVolume(name));

    [Fact]
    public void KeepsWhichTorrentFilesAreWanted()
    {
        var link = new Uri("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a");
        var chosen = new Uri(TorrentFileChoice.Chosen(link, [0, 2], all: 3));
        Assert.Equal(new HashSet<int> { 0, 2 }, TorrentFileChoice.ChosenFiles(chosen));
        Assert.True(TorrentFileChoice.TryReadMagnet(chosen, out _));
        Assert.Null(TorrentFileChoice.ChosenFiles(new Uri(TorrentFileChoice.Chosen(link, [0, 1, 2], all: 3))));
    }

    [Fact]
    public void ReadsSynologyTasks()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""{"id":"dbid_1","status":"downloading","size":100,"additional":{"transfer":{"size_downloaded":40,"speed_download":5},"detail":{"uri":"https://x.example/a.zip","destination":"downloads"}}}""");
        var task = SynologyQueue.Read(json.RootElement);
        Assert.Equal(("dbid_1", QueueState.Downloading, 40L, (long?)100L, "downloads"), (task.Id, task.State, task.Done, task.Total, task.Folder));
    }

    [Fact]
    public void ReadsMegaFolders()
    {
        var folderKey = new byte[16];
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = folderKey;
        var rawKey = new byte[32];
        rawKey[0] = 9;
        var wrapped = System.Buffers.Text.Base64Url.EncodeToString(aes.EncryptEcb(rawKey, System.Security.Cryptography.PaddingMode.None));
        var nodes = MegaApi.ReadFolder($$"""[{"f":[{"h":"f1","t":0,"a":"","k":"owner:{{wrapped}}"},{"h":"s1","t":2,"k":"x:y"}]}]""", folderKey);
        var file = Assert.Single(nodes);
        Assert.Equal("f1", file.Handle);
        Assert.Equal(rawKey, file.RawKey);
    }

    [Fact]
    public void KnowsMegaFolderLinks()
    {
        var key = new string('A', 22);
        Assert.True(MegaSite.TryReadFolder(new Uri($"https://mega.nz/folder/abc#{key}"), out var folder, out _));
        Assert.Equal("abc", folder);
        Assert.False(MegaSite.TryReadFolder(new Uri($"https://mega.nz/folder/abc#{key}/file/f1"), out _, out _));
        Assert.True(new MegaSite(_http, UserAgent).TryParseFile(new Uri($"https://mega.nz/folder/abc#{key}/file/f1"), out var file, out _));
        Assert.Equal("f1", file?.Key);
    }

    [Fact]
    public void AddsAnEndingOnlyAtTheEnd()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = Path.Combine(folder, "set");
            var chosen = DownloadTarget.Choose(target, "key", ExistingFile.Rename);
            File.WriteAllText(PartFile.PartPath(target), "tar");
            var final = chosen.Complete(".tar");
            DownloadTarget.Release(target);
            Assert.Equal(target + ".tar", final);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DecryptsMegaFromAnyByte()
    {
        var plain = MegaFixture.Bytes(300_000);
        var (rawKey, encrypted) = MegaFixture.Encrypt(plain);
        using var whole = new MegaCipher(rawKey);
        var decrypted = encrypted.ToArray();
        whole.Decrypt(decrypted);
        Assert.Equal(plain, decrypted);
        Assert.True(whole.Matches());

        using var fromTheMiddle = new MegaCipher(rawKey);
        fromTheMiddle.Count(plain.AsSpan(0, 131_093));
        var rest = encrypted[131_093..];
        fromTheMiddle.Decrypt(rest);
        Assert.Equal(plain[131_093..], rest);
        Assert.True(fromTheMiddle.Matches());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(131_072)]
    [InlineData(131_089)]
    [InlineData(400_000)]
    public void ChecksAMegaFileAgainstItsKey(int length)
    {
        var plain = MegaFixture.Bytes(length);
        var (rawKey, encrypted) = MegaFixture.Encrypt(plain);
        using var right = new MegaCipher(rawKey);
        right.Decrypt(encrypted);
        Assert.True(right.Matches());
        if (length > 0)
        {
            encrypted[^1] ^= 1;
            using var corrupted = new MegaCipher(rawKey);
            corrupted.Decrypt(encrypted);
            Assert.False(corrupted.Matches());
        }
        rawKey[^1] ^= 1;
        using var wrongKey = new MegaCipher(rawKey);
        wrongKey.Count(plain);
        Assert.False(wrongKey.Matches());
    }

    [Fact]
    public void MegaAnswersAreRead()
    {
        var (url, size, at) = MegaApi.ReadFile("""[{"s":12,"at":"abc","g":"https://g.example/x"}]""");
        Assert.Equal((new Uri("https://g.example/x"), 12L, "abc"), (url, size, at));
        Assert.Throws<StageException>(() => MegaApi.ReadFile("<html>"));
    }

    [Theory]
    [InlineData("[-3]", true, "busy")]
    [InlineData("-3", true, "busy")]
    [InlineData("[-9]", false, "no such file")]
    [InlineData("[-17]", false, "quota")]
    [InlineData("[-99]", false, "-99")]
    public void SaysWhatMegasErrorCodesMean(string answer, bool retryable, string word)
    {
        var e = Assert.Throws<StageException>(() => MegaApi.ReadFile(answer));
        Assert.Equal(retryable, e.IsRetryable);
        Assert.Contains(word, e.Message, StringComparison.Ordinal);
        Assert.Equal(Stage.Link, Assert.Throws<StageException>(() => MegaApi.ReadFolder(answer, new byte[16])).Stage);
    }

    [Theory]
    [InlineData("a.zip", true)]
    [InlineData("a.TAR.GZ", true)]
    [InlineData("a.tgz", true)]
    [InlineData("a.exe", false)]
    public void KnowsArchives(string name, bool archive) => Assert.Equal(archive, FinishedFileWork.IsArchive(name));

    [Fact]
    public void UnpacksBesideTheFile()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Equal(Path.Combine(folder, "a"), FinishedFileWork.FreeFolder(Path.Combine(folder, "a.tar.gz")));
            Directory.CreateDirectory(Path.Combine(folder, "a"));
            Assert.Equal(Path.Combine(folder, "a (1)"), FinishedFileWork.FreeFolder(Path.Combine(folder, "a.zip")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TakesMagnetLinks()
    {
        const string Hash = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a";
        var site = new TorrentSite(_http, UserAgent, claimsFiles: true, 0, 0);
        var link = new Uri($"magnet:?xt=urn:btih:{Hash}&dn=Some.File.iso");
        Assert.True(site.Claims(link));
        Assert.True(site.TryParseFile(link, out var file, out var reason), reason);
        Assert.Equal("Some.File.iso", file.Label);
        Assert.Equal(Hash, file.Key, StringComparer.OrdinalIgnoreCase);
        Assert.False(site.TryParseFile(new Uri("magnet:?dn=nothing"), out _, out _));
    }

    [Fact]
    public void ATorrentFileIsOnlyATorrentWithThePluginOn()
    {
        var link = new Uri("https://example.com/a.torrent");
        Assert.True(new TorrentSite(_http, UserAgent, claimsFiles: true, 0, 0).Claims(link));
        Assert.False(new TorrentSite(_http, UserAgent, claimsFiles: false, 0, 0).Claims(link));
        Assert.True(new TorrentSite(_http, UserAgent, claimsFiles: false, 0, 0).Claims(new Uri("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a")));
    }

    [Fact]
    public void AnOffPluginOffersAMagnetLink() =>
        Assert.NotNull(new SiteCatalog([new SiteEntry(new TorrentSite(_http, UserAgent, claimsFiles: false, 0, 0), IsOn: false)])
            .OffSiteOf("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a"));

    [Fact]
    public void ALinkWithNoPathHasAName() => Assert.Equal("", FileLink.LastSegment(new Uri("magnet:?xt=urn:btih:abc")));

    [Fact]
    public void ATorrentOfOneFileBecomesThatFile()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var single = Path.Combine(folder, "single.ferry-part");
            Directory.CreateDirectory(single);
            File.WriteAllText(Path.Combine(single, "a.iso"), "bytes");
            PartFile.BecomeOneFile(single);
            Assert.Equal("bytes", File.ReadAllText(single));

            var many = Path.Combine(folder, "many.ferry-part");
            Directory.CreateDirectory(many);
            File.WriteAllText(Path.Combine(many, "a"), "1");
            File.WriteAllText(Path.Combine(many, "b"), "2");
            PartFile.BecomeOneFile(many);
            Assert.True(Directory.Exists(many));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void APartCanBeAFolder()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = Path.Combine(folder, "set");
            var chosen = DownloadTarget.Choose(target, "key", ExistingFile.Rename);
            Directory.CreateDirectory(PartFile.PartPath(target));
            File.WriteAllText(Path.Combine(PartFile.PartPath(target), "a"), "1");
            Directory.CreateDirectory(target); // something took the name meanwhile
            var final = chosen.Complete();
            DownloadTarget.Release(target);
            Assert.Equal(Path.Combine(folder, "set (1)"), final);
            Assert.True(File.Exists(Path.Combine(final, "a")));

            Directory.CreateDirectory(PartFile.PartPath(target));
            PartFile.Delete(target);
            Assert.False(Directory.Exists(PartFile.PartPath(target)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void EveryPluginHasASwitchInTheTable()
    {
        foreach (var plugin in Ferry.Plugins.Official.OfficialSites.Plugins)
            Assert.True(SettingKey.TryFind(plugin.Enabled.Name, out _), plugin.Name);
    }

    /// <summary>What the app shows with no switch has to be on without one, and has no key to ask for.</summary>
    [Fact]
    public void WhatIsBuiltInIsOnFromTheStartAndAsksForNothing()
    {
        var builtIn = Ferry.Plugins.Official.OfficialSites.Plugins.Where(plugin => plugin.BuiltIn).ToList();
        Assert.NotEmpty(builtIn);
        foreach (var plugin in builtIn)
        {
            Assert.True(plugin.Enabled.Default == "true", plugin.Name);
            Assert.True(plugin.Secrets.Count == 0, plugin.Name);
            Assert.True(plugin.About.Group == PluginGroup.Links, plugin.Name);
        }
    }
}
