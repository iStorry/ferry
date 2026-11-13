using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Server;

/// <summary>
/// Server mode's lister: a link to many files is read on the server, by the agent's own listers, so no site hears from
/// this PC. It comes after the listers that must run here (<see cref="SitePlugin.ReadsListsHere"/>).
/// </summary>
/// <remarks>While the server can't be reached it fails with a <see cref="Core.StageException"/>; the queue's host keeps
/// links until it can be.</remarks>
public sealed class RemoteLister(RemoteQueue agent) : IFileLister
{
    public Task<FileList?> TryListAsync(Uri link, RequestDetails details, CancellationToken cancel) => agent.ExpandAsync(link, details, cancel);
}
