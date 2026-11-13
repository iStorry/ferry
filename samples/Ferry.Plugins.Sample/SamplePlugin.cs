using System.Diagnostics.CodeAnalysis;
using Ferry.Core.Http;
using Ferry.Core.Plugins;
using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Sample;

/// <summary>
/// A plugin to copy from: one site, files.example, whose page for a file ("/get/{name}") isn't the file itself
/// ("/files/{name}"). Pack it with tools/Ferry.PluginPack.
/// </summary>
public sealed class SamplePlugin : IFerryPlugin
{
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_SAMPLE", SettingType.Boolean, "true",
        "Files on files.example, as an example of a plugin.")
    { Title = "Sample" };

    public string Id => "ferry.sample";

    public string Publisher => "Ferry";

    public LinkClaims Claims { get; } = new([SampleSite.Host], []);

    public SitePlugin Plugin { get; } = new("Sample", Enabled)
    {
        About = new(PluginGroup.Links, "", [SampleSite.Host], ["It is an example: files.example is nobody's site."]),
        SiteIds = [SampleSite.SiteId],
        Sites = context => [new SampleSite(context.Http, context.UserAgent)],
    };
}

/// <summary>files.example: a link to a file's page, downloaded as the file.</summary>
public sealed class SampleSite(HttpFetcher http, string userAgent) : ISite
{
    public const string SiteId = "sample";
    public const string Host = "files.example";
    private const string PagePath = "/get/";

    public string Id => SiteId;

    public string Name => "Sample";

    public bool Claims(Uri link) => LinkProbe.IsWebLink(link) && link.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);

    public bool TryParseFile(Uri link, [NotNullWhen(true)] out FileLink? file, [NotNullWhen(false)] out string? reason)
    {
        var name = link.AbsolutePath.StartsWith(PagePath, StringComparison.Ordinal) ? link.AbsolutePath[PagePath.Length..] : "";
        file = name.Length > 0 && !name.Contains('/', StringComparison.Ordinal) ? new FileLink(SiteId, link, name) : null;
        reason = file is null ? $"\"{link}\" is not a file's page on {Host} (expected {Host}{PagePath}...)." : null;
        return file is not null;
    }

    public Task<ResolvedFile> ResolveAsync(FileLink file, RequestDetails details, CancellationToken cancel) =>
        LinkProbe.ResolveFileAsync(http, userAgent, file, new Uri(file.Original, "/files/" + file.Key), details,
                                   "The file is gone: its page answered instead.", cancel);
}
