namespace Ferry.Core;

/// <summary>
/// A check a site put in the way that only a person in a browser can pass: a picture of characters to type, say. Ferry
/// never answers one itself. It shows the person the site's own page, and what the site then gives their browser is used.
/// </summary>
/// <param name="Page">the page that shows the check, https only; it is opened as it is</param>
/// <param name="Cookie">the name of the cookie the site sets on <paramref name="Page"/>'s host once the check is passed</param>
public sealed record PersonCheck(Uri Page, string Cookie)
{
    /// <summary>Whether Ferry will show it: a web page over https, and a cookie with a name.</summary>
    public bool IsUsable => Page is { IsAbsoluteUri: true } && Page.Scheme == Uri.UriSchemeHttps && Cookie.Length is > 0 and <= 128;
}

/// <summary>A <see cref="PersonCheck"/> a person passed.</summary>
/// <param name="Page">the check's page</param>
/// <param name="Cookie">the cookie's name, as the check gave it</param>
/// <param name="Value">what the site set it to</param>
/// <param name="UserAgent">the browser that passed: a site may take the cookie from no other</param>
public sealed record PassedCheck(Uri Page, string Cookie, string Value, string UserAgent);
