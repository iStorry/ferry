using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.DevReleases;

/// <summary>What the Dev Releases plugin adds to Ferry: pages about a file or a release on developers' sites, downloaded as the file.</summary>
public static class DevReleasesPlugin
{
    /// <summary>On a new install too: without it, these links can only be refused. Links that already lead to a file aren't its, so they work either way.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_DEV_RELEASES", SettingType.Boolean, "true",
        "Release and file pages from GitHub, GitLab, Hugging Face and SourceForge.")
    { Title = "Dev Releases" };

    public static SitePlugin Plugin { get; } = new("Dev Releases", Enabled)
    {
        BuiltIn = true,
        About = new(PluginGroup.Links, "\uE943", ["github.com", "gitlab.com", "huggingface.co", "sourceforge.net"], ["GitHub allows 60 lookups an hour without a login."]),
        SiteIds = [GitHubSite.SiteId, RepoFileSites.GitLabId, RepoFileSites.HuggingFaceId, RepoFileSites.SourceForgeId],
        Sites = context => [new GitHubSite(context.Http, context.UserAgent, For(context)), RepoFileSites.GitLab(context.Http, context.UserAgent),
                            RepoFileSites.HuggingFace(context.Http, context.UserAgent), RepoFileSites.SourceForge(context.Http, context.UserAgent)],
    };

    /// <summary>Whose file a link to a whole release means: a server's agent fetches for the Windows PC it serves; anywhere else, the machine it runs on.</summary>
    private static TargetSystem For(PluginContext context) => context.IsAgent ? TargetSystem.Windows : TargetSystem.Here;
}
