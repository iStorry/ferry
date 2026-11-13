namespace Ferry.Core.Sites;

/// <summary>The hosts a site answers for, so that every site tells its links apart the same way.</summary>
public sealed class HostSet
{
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _under;

    private HostSet(IEnumerable<string> exact, string[] under)
    {
        _exact.UnionWith(exact);
        _under = under;
    }

    /// <summary>These hosts, each with and without "www.".</summary>
    public static HostSet Of(params string[] hosts) =>
        new(hosts.SelectMany(host => new[] { host, "www." + host }), []);

    /// <summary>These hosts and every host under them ("cdn.example.com" is under "example.com").</summary>
    public static HostSet Under(params string[] hosts) => new(hosts, hosts);

    /// <summary>Whether <paramref name="link"/> is a web link on one of the hosts.</summary>
    public bool Claims(Uri link) => LinkProbe.IsWebLink(link) && Contains(link.Host);

    public bool Contains(string host) =>
        _exact.Contains(host) || _under.Any(under => host.EndsWith("." + under, StringComparison.OrdinalIgnoreCase));
}
