using System.Diagnostics.CodeAnalysis;

namespace Ferry.Core.Http;

/// <summary>
/// What a server wants with a request besides the link: where it came from and who is asking. Cookies and headers can
/// be logins, so these are never written to queue.json, logs or output (<see cref="ToString"/> says nothing of them).
/// </summary>
/// <param name="Headers">more headers, by name; these replace what Ferry would send under that name</param>
public sealed record RequestDetails(Uri? Referer = null, string? Cookies = null, Dictionary<string, string>? Headers = null)
{
    internal const string CookieHeader = "Cookie";

    // Ferry's own to set, or set by the fields above
    private static readonly HashSet<string> NotHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        CookieHeader, "Referer", "Host", "Range", "Content-Length", "Connection", "Transfer-Encoding",
    };

    public static RequestDetails None { get; } = new();

    public bool IsEmpty => Referer is null && string.IsNullOrEmpty(Cookies) && Headers is not { Count: > 0 };

    public void ApplyTo(HttpRequestMessage request)
    {
        if (Referer is not null)
            request.Headers.Referrer = Referer;
        if (!string.IsNullOrEmpty(Cookies))
            request.Headers.TryAddWithoutValidation(CookieHeader, Cookies);
        foreach (var (name, value) in Headers ?? [])
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>Details from what a person typed; false, with the reason for them, when something can't be used.</summary>
    /// <param name="headerLines">"Name: value", one each; blank ones are passed over</param>
    public static bool TryCreate(string? referer, string? cookies, IEnumerable<string> headerLines,
                                 [NotNullWhen(true)] out RequestDetails? details, [NotNullWhen(false)] out string? reason)
    {
        details = null;
        Uri? from = null;
        if (referer?.Trim() is { Length: > 0 } text
            && (!Uri.TryCreate(text, UriKind.Absolute, out from) || (from.Scheme != Uri.UriSchemeHttps && from.Scheme != Uri.UriSchemeHttp)))
        {
            reason = "The referer is not a web address (https://...).";
            return false;
        }
        var cookie = cookies?.Trim();
        if (cookie is not null && cookie.Any(char.IsControl))
        {
            reason = "The cookies have a line break or another control character in them.";
            return false;
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headerLines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (!TryReadHeader(line, out var name, out var value, out reason))
                return false;
            headers[name] = value;
        }
        details = new RequestDetails(from, cookie is { Length: > 0 } ? cookie : null, headers.Count > 0 ? headers : null);
        reason = null;
        return true;
    }

    /// <summary>Says nothing of the values: this ends up wherever an object is printed.</summary>
    public override string ToString() => IsEmpty ? "no request details" : "request details (hidden)";

    private static bool TryReadHeader(string line, [NotNullWhen(true)] out string? name, [NotNullWhen(true)] out string? value,
                                      [NotNullWhen(false)] out string? reason)
    {
        (name, value, reason) = (null, null, null);
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            reason = $"\"{Shown(line)}\" is not a header: write it as Name: value.";
            return false;
        }
        var (read, text) = (line[..colon].Trim(), line[(colon + 1)..].Trim());
        if (NotHeaders.Contains(read))
        {
            reason = $"{read} can't be set as a header here.";
            return false;
        }
        using var probe = new HttpRequestMessage();
        if (!probe.Headers.TryAddWithoutValidation(read, text))
        {
            reason = $"\"{Shown(read)}\" can't be sent as a header.";
            return false;
        }
        (name, value) = (read, text);
        return true;
    }

    // only the name's side of the line: what is after the colon may be a password
    private static string Shown(string line) => line.Split(':')[0];
}
