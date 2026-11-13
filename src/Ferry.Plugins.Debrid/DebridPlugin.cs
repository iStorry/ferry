using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Debrid;

/// <summary>What the Debrid plugin adds to Ferry: links on hosts the user lists go through their debrid service, for a fast direct link.</summary>
public static class DebridPlugin
{
    internal const string RealDebrid = "real-debrid";
    internal const string AllDebrid = "alldebrid";

    /// <summary>Off on a new install: it needs an account and a key.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_DEBRID", SettingType.Boolean, "false",
        "Links on the hosts you list go through your debrid service.")
    { Title = "Debrid" };

    private static readonly SettingKey Service = new("FERRY_DEBRID_SERVICE", SettingType.Choice, RealDebrid,
        "The debrid service you have an account with.")
    {
        Title = "Service",
        Choices = [RealDebrid, AllDebrid],
    };

    private static readonly SettingKey Hosts = new("FERRY_DEBRID_HOSTS", SettingType.Hosts, "rapidgator.net,nitroflare.com,1fichier.com,turbobit.net,katfile.com,ddownload.com",
        "Which hosts' links go through the service. Links on any other host are downloaded as usual.")
    { Title = "Hosts to send" };

    private static readonly PluginSecret ApiKey = new("debrid-api-key", "API key", "From your account on the service's site. Kept encrypted on this PC, so Debrid works here and not on a server.");

    /// <summary>
    /// Its links aren't its own: with the plugin off or no key, whoever would have taken them still does. Asked first, so it
    /// comes before them. In server mode the PC asks the service, so the key stays here, and only the direct link goes to the server.
    /// </summary>
    public static SitePlugin Plugin { get; } = new("Debrid", Enabled)
    {
        About = new(PluginGroup.Links, "\uE945", ["The hosts you list"], ["In server mode this PC asks the service and the server gets the direct link, so a resume after that link expires fails."]),
        SiteIds = [DebridSite.SiteId],
        KeepsLinksWhileOff = false,
        Options = [Service, Hosts],
        Secrets = [ApiKey],
        Sites = context => Site(context) is { } site ? [site] : [],
        Listers = context => context.DownloadsElsewhere && Site(context) is { } site ? [new DirectLinkLister(site)] : [],
        ReadsListsHere = true,
    };

    private static DebridSite? Site(PluginContext context) =>
        context.Secret(ApiKey) is { } key
            ? new DebridSite(context.Http, context.UserAgent, context.Settings.Text(Service), key, [.. context.Settings.Text(Hosts).Split(',', StringSplitOptions.RemoveEmptyEntries)])
            : null;

    /// <summary>A link on a listed host, as the service's direct link, for a server to download.</summary>
    private sealed class DirectLinkLister(DebridSite site) : IFileLister
    {
        public async Task<FileList?> TryListAsync(Uri link, Ferry.Core.Http.RequestDetails details, CancellationToken cancel)
        {
            if (!site.Claims(link) || !site.TryParseFile(link, out var file, out _))
                return null;
            var resolved = await site.ResolveAsync(file, details, cancel);
            return new FileList(null, [resolved.Url.ToString()]);
        }
    }
}
