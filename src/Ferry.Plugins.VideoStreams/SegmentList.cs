namespace Ferry.Plugins.VideoStreams;

/// <summary>One piece of a stream.</summary>
/// <param name="Range">the bytes of <paramref name="Url"/> it is, when it isn't all of them</param>
/// <param name="Key">the key it is encrypted with (AES-128), when it is</param>
/// <param name="Iv">what starts its decryption, when <paramref name="Key"/> is set</param>
/// <param name="Map">
/// the initialization section to write before this piece: on the first piece, and on each piece after it changes
/// (fragmented MP4; a stream of transport-stream pieces has none)
/// </param>
internal sealed record MediaSegment(Uri Url, (long From, long To)? Range, Uri? Key, byte[]? Iv, MediaSegment? Map = null);

/// <summary>
/// One track of a stream as its pieces, which written one after another are a file that plays. An HLS media playlist
/// and a DASH representation both come to this.
/// </summary>
internal record SegmentList(IReadOnlyList<MediaSegment> Segments)
{
    /// <summary>Where the track's first initialization section is; null for a track that has none.</summary>
    public Uri? Init => Segments is [{ Map: { } first }, ..] ? first.Url : null;
}

/// <summary>What a stream's tracks are kept in, as the file's ending and as ffmpeg knows the format.</summary>
internal sealed record MediaContainer(string Ending, string Format)
{
    public static MediaContainer Mp4 { get; } = new(".mp4", "mp4");

    public static MediaContainer WebM { get; } = new(".webm", "webm");

    /// <summary>The container a DASH representation's mimeType ("video/webm", "video/mp4") names; MP4 when it names none.</summary>
    public static MediaContainer OfMimeType(string? mimeType) =>
        mimeType?.EndsWith("/webm", StringComparison.OrdinalIgnoreCase) == true ? WebM : Mp4;
}
