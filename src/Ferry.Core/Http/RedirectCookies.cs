namespace Ferry.Core.Http;

/// <summary>
/// The cookies the redirects of one request set, for its later hops to the same site: a guest link that sets a cookie
/// and asks for it on the next hop would otherwise redirect for ever. Each request has its own, kept for no longer than
/// it runs. A cookie's name and value only: nothing past the hops of one request needs its attributes.
/// </summary>
internal sealed class RedirectCookies
{
    private const string SetCookieHeader = "Set-Cookie";
    private readonly List<(Uri Site, string Name, string Value)> _cookies = [];

    /// <summary>Take in what <paramref name="response"/>, from <paramref name="site"/>, sets.</summary>
    public void Note(HttpResponseMessage response, Uri site)
    {
        if (!response.Headers.TryGetValues(SetCookieHeader, out var lines))
            return;
        foreach (var line in lines)
        {
            var pair = line.Split(';', 2)[0];
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                continue; // not a cookie: nothing to carry
            var (name, value) = (pair[..equals].Trim(), pair[(equals + 1)..].Trim());
            _cookies.RemoveAll(cookie => cookie.Name == name && HttpFetcher.IsSameSite(cookie.Site, site));
            _cookies.Add((site, name, value));
        }
    }

    /// <summary>Add to <paramref name="next"/> what its own site set, over any of those names it already has.</summary>
    public void ApplyTo(HttpRequestMessage next)
    {
        var own = _cookies.Where(cookie => HttpFetcher.IsSameSite(cookie.Site, next.RequestUri)).ToList();
        if (own.Count == 0)
            return;
        var given = next.Headers.TryGetValues(RequestDetails.CookieHeader, out var values)
            ? values.SelectMany(value => value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : [];
        var kept = given.Where(pair => !own.Exists(cookie => pair.StartsWith(cookie.Name + "=", StringComparison.Ordinal)));
        var header = string.Join("; ", kept.Concat(own.Select(cookie => $"{cookie.Name}={cookie.Value}")));
        next.Headers.Remove(RequestDetails.CookieHeader);
        next.Headers.TryAddWithoutValidation(RequestDetails.CookieHeader, header);
    }
}
