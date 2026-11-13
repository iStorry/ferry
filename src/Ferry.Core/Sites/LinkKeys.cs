namespace Ferry.Core.Sites;

/// <summary>Keys for <see cref="FileLink"/>s whose link is the file itself.</summary>
public static class LinkKeys
{
    /// <summary>The link without what follows "#", which never reaches the server, so two links to one file get one key.</summary>
    public static string WithoutFragment(Uri link) => new UriBuilder(link) { Fragment = "" }.Uri.ToString();
}
