using Ferry.Core;
using Ferry.Core.Library;

namespace Ferry.Plugins.Server;

/// <summary>The Library page's update check, made by the agent: a plugin's site is searched from the server, never from this PC.</summary>
public sealed class RemoteTitleSearch(RemoteQueue agent)
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(1);

    /// <summary>What the site lists for a title, newest first.</summary>
    /// <exception cref="StageException">the server, or the site through it, couldn't answer</exception>
    public async Task<IReadOnlyList<FoundTitle>> SearchAsync(string name, CancellationToken cancel)
    {
        try
        {
            return (await agent.RequestAsync(new AgentRequest(0, AgentCommand.TitleSearch, Name: name), SearchTimeout, cancel)).Titles ?? [];
        }
        catch (InvalidOperationException e)
        {
            throw new StageException(Stage.Server, e.Message, inner: e); // the agent couldn't: for the user as any failure to read
        }
    }
}
