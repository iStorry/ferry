using System.Globalization;
using System.Text.RegularExpressions;
using Ferry.Core;

namespace Ferry.Plugins.VideoStreams;

/// <summary>An HLS playlist: either the qualities on offer, or the pieces of one.</summary>
/// <param name="Best">a playlist of qualities: the best one's own playlist; else null</param>
/// <param name="Audio">a playlist of qualities: the best one's sound, when that is a playlist of its own; else null</param>
internal sealed partial record HlsPlaylist(Uri? Best, Uri? Audio, IReadOnlyList<MediaSegment> Segments, bool IsLive) : SegmentList(Segments)
{
    private const int IvBytes = 16;
    private const char ByteOrderMark = '﻿';
    private const string Header = "#EXTM3U";
    private const string Variant = "#EXT-X-STREAM-INF";
    private const string Rendition = "#EXT-X-MEDIA:";
    private const string Sequence = "#EXT-X-MEDIA-SEQUENCE:";
    private const string Map = "#EXT-X-MAP:";
    private const string Key = "#EXT-X-KEY:";
    private const string ByteRange = "#EXT-X-BYTERANGE:";
    private const string End = "#EXT-X-ENDLIST";

    /// <exception cref="StageException">it isn't a playlist, or its stream is protected by DRM</exception>
    public static HlsPlaylist Parse(string text, Uri at)
    {
        // some servers write a byte order mark first, which is no part of the playlist
        var lines = text.TrimStart(ByteOrderMark).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != Header)
            throw new StageException(Stage.Resolve, "This link isn't an HLS playlist.");
        try
        {
            return lines.Any(line => line.StartsWith(Variant, StringComparison.Ordinal)) ? Qualities(lines, at) : Pieces(lines, at);
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new StageException(Stage.Resolve, "This playlist couldn't be read.", inner: e);
        }
    }

    /// <summary>The best variant, and the audio rendition it names when that is a playlist of its own (else its sound is in the variant).</summary>
    private static HlsPlaylist Qualities(string[] lines, Uri at)
    {
        var (best, bandwidth, group) = ((Uri?)null, -1L, (string?)null);
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].StartsWith(Variant, StringComparison.Ordinal) || lines[i + 1].StartsWith('#'))
                continue;
            var offered = Attribute(lines[i], "BANDWIDTH") is { } text && long.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : 0;
            if (offered > bandwidth)
                (best, bandwidth, group) = (new Uri(at, lines[i + 1]), offered, Attribute(lines[i], "AUDIO"));
        }
        return new HlsPlaylist(best, group is null ? null : AudioOf(lines, group, at), [], IsLive: false);
    }

    /// <summary>The audio group's own playlist: its default rendition's, else the first's that has one.</summary>
    private static Uri? AudioOf(string[] lines, string group, Uri at)
    {
        Uri? first = null;
        foreach (var line in lines.Where(line => line.StartsWith(Rendition, StringComparison.Ordinal)))
        {
            if (Attribute(line, "TYPE") != "AUDIO" || Attribute(line, "GROUP-ID") != group || Attribute(line, "URI") is not { } uri)
                continue;
            if (Attribute(line, "DEFAULT") == "YES")
                return new Uri(at, uri);
            first ??= new Uri(at, uri);
        }
        return first;
    }

    private static HlsPlaylist Pieces(string[] lines, Uri at)
    {
        var segments = new List<MediaSegment>();
        var (map, key, iv, range, nextFrom) = ((MediaSegment?)null, (Uri?)null, (byte[]?)null, ((long, long)?)null, 0L);
        var sequence = 0L;
        var mapChanged = false;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith(Sequence, StringComparison.Ordinal))
                sequence = long.Parse(line.AsSpan(Sequence.Length), CultureInfo.InvariantCulture);
            else if (line.StartsWith(Map, StringComparison.Ordinal))
                (map, mapChanged) = (ReadMap(line, at) ?? map, true);
            else if (line.StartsWith(Key, StringComparison.Ordinal))
                (key, iv) = ReadKey(line, at);
            else if (line.StartsWith(ByteRange, StringComparison.Ordinal))
                (range, nextFrom) = ReadRange(line[ByteRange.Length..], nextFrom);
            else if (!line.StartsWith('#'))
            {
                // the key in force covers the initialization section too, under the IV of the piece that follows it
                var pieceIv = key is null ? null : iv ?? SequenceIv(sequence + segments.Count);
                var init = mapChanged && map is { } section ? section with { Key = key, Iv = pieceIv } : null;
                segments.Add(new MediaSegment(new Uri(at, line), range, key, pieceIv, init));
                (range, mapChanged) = (null, false);
            }
        }
        return new HlsPlaylist(null, null, segments, IsLive: !lines.Contains(End));
    }

    /// <summary>The initialization section a MAP tag names, with the bytes of it when it is part of a larger file.</summary>
    private static MediaSegment? ReadMap(string line, Uri at) =>
        Attribute(line, "URI") is { } uri
            ? new MediaSegment(new Uri(at, uri), Attribute(line, "BYTERANGE") is { } bytes ? ReadRange(bytes, 0).Range : null, null, null)
            : null;

    /// <summary>Only plain AES-128 can be read: anything else is DRM, which Ferry doesn't get around.</summary>
    private static (Uri?, byte[]?) ReadKey(string line, Uri at)
    {
        var method = Attribute(line, "METHOD");
        if (method == "NONE")
            return (null, null);
        if (method != "AES-128" || Attribute(line, "KEYFORMAT") is not (null or "identity") || Attribute(line, "URI") is not { } uri)
            throw new StageException(Stage.Resolve, "This stream is protected by DRM, so Ferry won't download it.");
        return (new Uri(at, uri), Attribute(line, "IV") is { } iv ? Convert.FromHexString(iv.AsSpan(2)) : null);
    }

    private static ((long From, long To)? Range, long NextFrom) ReadRange(string text, long nextFrom)
    {
        var parts = text.Split('@');
        var length = long.Parse(parts[0], CultureInfo.InvariantCulture);
        var from = parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : nextFrom;
        return ((from, from + length - 1), from + length);
    }

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[IvBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(IvBytes - sizeof(long)), sequence);
        return iv;
    }

    private static string? Attribute(string line, string name)
    {
        foreach (Match match in Attributes().Matches(line))
        {
            if (match.Groups["name"].Value == name)
                return match.Groups["value"].Value.Trim('"');
        }
        return null;
    }

    [GeneratedRegex("""(?<name>[A-Z0-9-]+)=(?<value>"[^"]*"|[^,]*)""")]
    private static partial Regex Attributes();
}
