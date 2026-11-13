using System.Runtime.CompilerServices;
using Ferry.Core.Http;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.Official;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>What the app and the agent do at start, with the tests' own site among the plugins, and the catalogs tests build.</summary>
internal static class TestSites
{
    [ModuleInitializer]
    internal static void RegisterPlugins()
    {
        OfficialSites.Register();
        ServerKeys.Register();
        OfficialSites.TryAdd(FakeSitePlugin.Plugin, out _);
    }

    /// <summary>The fake as the only site: a link elsewhere is nobody's.</summary>
    public static SiteCatalog Only(HttpFetcher http, FakeSiteOptions options) =>
        new([new SiteEntry(new FakeFileSite(http, options, SettingKey.BrowserUserAgent.Default))]);

    /// <exception cref="StageException">not a file link of the fake's (<see cref="Stage.Link"/>)</exception>
    public static FileLink Link(string text)
    {
        var site = new FakeFileSite(null!, null!, "");
        return Uri.TryCreate(text, UriKind.Absolute, out var link) && site.Claims(link) && site.TryParseFile(link, out var file, out _)
            ? file
            : throw new StageException(Stage.Link, $"\"{text}\" is not a file link of the fake site.");
    }
}
