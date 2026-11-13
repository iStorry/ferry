using System.Diagnostics;
using System.Security.Cryptography;
using Ferry.Core.Download;
using Ferry.Core.Http;
using Xunit.Abstractions;

namespace Ferry.Core.Tests;

/// <summary>A large file over loopback: the bytes arrive intact and the code is not what limits the speed.</summary>
public sealed class DownloadSpeedTests(ITestOutputHelper output) : IDisposable
{
    private const int Size = 200 << 20;
    private readonly FakeSite _site = new();
    private readonly HttpFetcher _http = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("ferry_speed_").FullName;

    public void Dispose()
    {
        _http.Dispose();
        _site.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task LargeFileOverEightConnections()
    {
        var data = new byte[Size];
        new Random(3).NextBytes(data);
        _site.AddFile("big", "7", "big.bin", data);
        var site = _site.Options();
        var download = new SegmentedDownload(_http, TestSites.Only(_http, site), DownloadOptions.For(DownloadProfile.Aggressive));
        var clock = Stopwatch.StartNew();

        var result = await download.RunAsync(new DownloadRequest(TestSites.Link("https://files.test/f/big"), _folder), _ => { },
                                             CancellationToken.None);

        clock.Stop();
        await using var saved = File.OpenRead(result.Path);
        Assert.Equal(SHA256.HashData(data), await SHA256.HashDataAsync(saved));
        output.WriteLine($"{Size >> 20} MB in {clock.Elapsed.TotalSeconds:F2} s: {Size / 1048576.0 / clock.Elapsed.TotalSeconds:F0} MB/s");
    }
}
