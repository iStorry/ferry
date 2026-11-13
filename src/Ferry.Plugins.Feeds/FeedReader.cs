using System.Xml;
using System.Xml.Linq;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Feeds;

/// <summary>The files an RSS or Atom feed offers: its enclosures, and items whose own link has the look of a file.</summary>
internal static class FeedReader
{
    private static readonly string[] WebPageEndings = [".html", ".htm", ".php", ".aspx", ".jsp"];

    /// <exception cref="XmlException">it isn't a feed</exception>
    public static IReadOnlyList<Uri> Files(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return [.. XDocument.Load(reader).Descendants().Where(element => element.Name.LocalName is "item" or "entry").Select(FileOf).OfType<Uri>()];
    }

    private static Uri? FileOf(XElement item)
    {
        var inner = item.Elements().ToList();
        var links = inner.Where(element => element.Name.LocalName == "link").ToList();
        var enclosure = inner.Where(element => element.Name.LocalName == "enclosure").Select(element => Web((string?)element.Attribute("url")))
            .Concat(links.Where(link => (string?)link.Attribute("rel") == "enclosure").Select(link => Web((string?)link.Attribute("href"))))
            .OfType<Uri>().FirstOrDefault();
        return enclosure ?? links.Select(link => Web((string?)link.Attribute("href") ?? link.Value)).OfType<Uri>().FirstOrDefault(LooksLikeAFile);
    }

    private static Uri? Web(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var link) && LinkProbe.IsWebLink(link) ? link : null;

    private static bool LooksLikeAFile(Uri link) =>
        Path.HasExtension(link.AbsolutePath) && !WebPageEndings.Any(ending => link.AbsolutePath.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
}
