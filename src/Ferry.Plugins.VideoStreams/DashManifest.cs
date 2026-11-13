using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Ferry.Core;

namespace Ferry.Plugins.VideoStreams;

/// <summary>
/// A DASH manifest (.mpd): the best video and the best audio it offers, as lists of pieces. They can be one track, when
/// its video has its sound in it. Anything protected by DRM is refused, and so is a live stream.
/// </summary>
/// <param name="Audio">its own track of sound, when the video has none</param>
/// <param name="Container">what the video's pieces are kept in, which is what the file becomes</param>
internal sealed record DashManifest(SegmentList Video, SegmentList? Audio, MediaContainer Container)
{
    private const int MostPieces = 200_000;

    /// <summary>r="-1" on a timeline entry: repeat it until the next entry starts, or the period ends.</summary>
    private const int UntilNext = -1;

    /// <exception cref="StageException">it isn't a manifest Ferry can read, or it is live or protected</exception>
    public static DashManifest Parse(string xml, Uri at)
    {
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return Read(XDocument.Load(reader).Root ?? throw new XmlException("No manifest."), at);
        }
        catch (Exception e) when (e is XmlException or FormatException or OverflowException)
        {
            throw new StageException(Stage.Resolve, "This manifest couldn't be read.", inner: e);
        }
    }

    private static DashManifest Read(XElement root, Uri at)
    {
        if (root.Name.LocalName != "MPD")
            throw new StageException(Stage.Resolve, "This link isn't a DASH manifest.");
        if (Attr(root, "type") == "dynamic")
            throw new StageException(Stage.Resolve, "This stream is live, so it has no end to download to.");
        if (root.Descendants().Any(element => element.Name.LocalName == "ContentProtection"))
            throw new StageException(Stage.Resolve, "This stream is protected by DRM, so Ferry won't download it.");
        var period = Children(root, "Period").FirstOrDefault() ?? throw new StageException(Stage.Resolve, "This manifest has no video in it.");
        var periodBase = BaseOf(BaseOf(at, root), period);
        var seconds = Seconds(Attr(period, "duration")) ?? Seconds(Attr(root, "mediaPresentationDuration"));
        var sets = Children(period, "AdaptationSet").ToList();
        var video = Best(sets, "video") ?? throw new StageException(Stage.Resolve, "This manifest has no video in it.");
        var audio = Best(sets, "audio");
        return new DashManifest(Track(video, period, periodBase, seconds), audio is null ? null : Track(audio.Value, period, periodBase, seconds),
                                MediaContainer.OfMimeType(MimeType(video)));
    }

    /// <summary>The adaptation set and representation of that kind with the most bits.</summary>
    private static (XElement Set, XElement Representation)? Best(List<XElement> sets, string kind) =>
        sets.SelectMany(set => Children(set, "Representation").Where(item => IsKind(set, item, kind)).Select(item => (Set: set, Representation: item)))
            .OrderByDescending(pair => long.TryParse(Attr(pair.Representation, "bandwidth"), CultureInfo.InvariantCulture, out var bits) ? bits : 0)
            .Cast<(XElement, XElement)?>()
            .FirstOrDefault();

    private static bool IsKind(XElement set, XElement item, string kind) =>
        Attr(set, "contentType") == kind || MimeType((set, item))?.StartsWith(kind + "/", StringComparison.Ordinal) == true;

    private static string? MimeType((XElement Set, XElement Representation) chosen) =>
        Attr(chosen.Representation, "mimeType") ?? Attr(chosen.Set, "mimeType");

    private static SegmentList Track((XElement Set, XElement Representation) chosen, XElement period, Uri periodBase, double? seconds)
    {
        var (set, item) = chosen;
        var at = BaseOf(BaseOf(periodBase, set), item);
        var id = Attr(item, "id") ?? "";
        var bandwidth = Attr(item, "bandwidth") ?? "";
        var template = Nearest("SegmentTemplate", item, set, period);
        if (template is not null)
            return FromTemplate(template, at, id, bandwidth, seconds);
        var list = Nearest("SegmentList", item, set, period);
        return list is not null
            ? WithInit([.. Children(list, "SegmentURL").Select(piece => new MediaSegment(new Uri(at, Attr(piece, "media") ?? ""), null, null, null))],
                       UrlOf(Children(list, "Initialization").FirstOrDefault(), "sourceURL", at))
            : new SegmentList([new MediaSegment(at, null, null, null)]); // the whole track is one file
    }

    private static SegmentList FromTemplate(XElement template, Uri at, string id, string bandwidth, double? seconds)
    {
        var media = Attr(template, "media") ?? throw new StageException(Stage.Resolve, "This manifest doesn't say where its pieces are.");
        var init = Attr(template, "initialization") is { } first ? new Uri(at, Fill(first, id, bandwidth, 0, 0)) : null;
        var number = long.TryParse(Attr(template, "startNumber"), CultureInfo.InvariantCulture, out var start) ? start : 1;
        var scale = long.TryParse(Attr(template, "timescale"), CultureInfo.InvariantCulture, out var perSecond) ? perSecond : 1;
        var pieces = new List<MediaSegment>();
        void Add(long time) => pieces.Add(new MediaSegment(new Uri(at, Fill(media, id, bandwidth, number++, time)), null, null, null));
        if (Children(template, "SegmentTimeline").FirstOrDefault() is { } timeline)
        {
            var entries = Children(timeline, "S").ToList();
            var time = 0L;
            for (var n = 0; n < entries.Count; n++)
            {
                var entry = entries[n];
                time = long.TryParse(Attr(entry, "t"), CultureInfo.InvariantCulture, out var given) ? given : time;
                var length = long.Parse(Attr(entry, "d") ?? "0", CultureInfo.InvariantCulture);
                var repeats = int.TryParse(Attr(entry, "r"), CultureInfo.InvariantCulture, out var more) ? more : 0;
                if (repeats == UntilNext)
                    repeats = RepeatsUntil(EndOf(entries.ElementAtOrDefault(n + 1), seconds, scale), time, length);
                for (var i = 0; i <= repeats && pieces.Count < MostPieces; i++, time += length)
                    Add(time);
            }
        }
        else
        {
            var length = double.Parse(Attr(template, "duration") ?? throw new StageException(Stage.Resolve, "This manifest doesn't say how long its pieces are."), CultureInfo.InvariantCulture);
            var count = (long)Math.Ceiling((seconds ?? throw new StageException(Stage.Resolve, "This manifest doesn't say how long the video is.")) * scale / length);
            for (var i = 0L; i < Math.Min(count, MostPieces); i++)
                Add((long)(i * length));
        }
        return WithInit(pieces, init);
    }

    /// <summary>Where an open-ended timeline entry ends: at the next entry's start, or at the period's end (ISO 23009-1, 5.3.9.6.1).</summary>
    private static long EndOf(XElement? next, double? seconds, long scale)
    {
        if (next is not null)
        {
            return long.TryParse(Attr(next, "t"), CultureInfo.InvariantCulture, out var start)
                ? start
                : throw new StageException(Stage.Resolve, "This manifest doesn't say where one run of its pieces ends.");
        }
        return (long)((seconds ?? throw new StageException(Stage.Resolve, "This manifest doesn't say how long the video is.")) * scale);
    }

    /// <summary>How many more pieces of <paramref name="length"/>, after the first at <paramref name="time"/>, reach <paramref name="end"/>.</summary>
    private static int RepeatsUntil(long end, long time, long length) =>
        length <= 0 || end <= time ? 0 : (int)Math.Min(Math.Ceiling((end - time) / (double)length) - 1, MostPieces);

    /// <summary>The pieces as a track, with the initialization section, when there is one, before the first.</summary>
    private static SegmentList WithInit(List<MediaSegment> pieces, Uri? init)
    {
        if (init is not null && pieces.Count > 0)
            pieces[0] = pieces[0] with { Map = new MediaSegment(init, null, null, null) };
        return new SegmentList(pieces);
    }

    /// <summary>A template with what it asks for filled in: $Number$, $Time$, $Bandwidth$, $RepresentationID$, with a width such as $Number%05d$.</summary>
    internal static string Fill(string template, string id, string bandwidth, long number, long time)
    {
        var text = template.Replace("$RepresentationID$", id, StringComparison.Ordinal).Replace("$Bandwidth$", bandwidth, StringComparison.Ordinal);
        text = FillNumber(text, "Number", number);
        text = FillNumber(text, "Time", time);
        return text.Replace("$$", "$", StringComparison.Ordinal);
    }

    private static string FillNumber(string text, string name, long value)
    {
        var mark = $"${name}";
        for (var at = text.IndexOf(mark, StringComparison.Ordinal); at >= 0; at = text.IndexOf(mark, StringComparison.Ordinal))
        {
            var end = text.IndexOf('$', at + mark.Length);
            if (end < 0)
                break;
            var format = text[(at + mark.Length)..end];
            var width = format.StartsWith("%0", StringComparison.Ordinal) && format.EndsWith('d') && int.TryParse(format.AsSpan(2, format.Length - 3), CultureInfo.InvariantCulture, out var digits) ? digits : 0;
            text = string.Concat(text.AsSpan(0, at), value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'), text.AsSpan(end + 1));
        }
        return text;
    }

    private static Uri BaseOf(Uri at, XElement element) =>
        Children(element, "BaseURL").FirstOrDefault() is { } given && given.Value.Trim().Length > 0 ? new Uri(at, given.Value.Trim()) : at;

    private static Uri? UrlOf(XElement? element, string attribute, Uri at) =>
        element is not null && Attr(element, attribute) is { } text ? new Uri(at, text) : null;

    private static XElement? Nearest(string name, params XElement[] places) =>
        places.Select(place => Children(place, name).FirstOrDefault()).OfType<XElement>().FirstOrDefault();

    private static IEnumerable<XElement> Children(XElement element, string name) => element.Elements().Where(child => child.Name.LocalName == name);

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    private static double? Seconds(string? duration) => duration is null ? null : XmlConvert.ToTimeSpan(duration).TotalSeconds;
}
