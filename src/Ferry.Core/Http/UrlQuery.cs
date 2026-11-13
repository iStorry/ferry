namespace Ferry.Core.Http;

/// <summary>Setting query parameters on a URL without re-encoding the ones already there.</summary>
public static class UrlQuery
{
    public static Uri With(Uri url, params ReadOnlySpan<(string Key, string Value)> parameters)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, _) in parameters)
            keys.Add(key);
        var kept = url.Query.TrimStart('?')
                      .Split('&', StringSplitOptions.RemoveEmptyEntries)
                      .Where(pair => !keys.Contains(Uri.UnescapeDataString(pair.Split('=', 2)[0])));
        var added = new List<string>(parameters.Length);
        foreach (var (key, value) in parameters)
            added.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        return new UriBuilder(url) { Query = string.Join('&', kept.Concat(added)) }.Uri;
    }

    /// <summary>The value of the query parameter <paramref name="key"/>, decoded; null when the URL has none.</summary>
    public static string? Value(Uri url, string key) =>
        url.Query.TrimStart('?')
           .Split('&', StringSplitOptions.RemoveEmptyEntries)
           .Select(pair => pair.Split('=', 2))
           .Where(pair => pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == key)
           .Select(pair => Uri.UnescapeDataString(pair[1]))
           .FirstOrDefault();
}
