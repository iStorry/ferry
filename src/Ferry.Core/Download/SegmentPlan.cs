namespace Ferry.Core.Download;

/// <summary>
/// A byte range of the file, [<see cref="Start"/>, <see cref="End"/>), downloaded by one connection at a time.
/// Its fields change only under <see cref="SegmentPlan"/>'s lock.
/// </summary>
internal sealed class Segment(long start, long end, long done)
{
    public long Start { get; } = start;

    /// <summary>Can move down while the segment downloads, when another connection takes over its second half.</summary>
    public long End { get; set; } = end;

    /// <summary>Bytes from <see cref="Start"/> written to disk.</summary>
    public long Done { get; set; } = done;

    /// <summary>Bytes from <see cref="Start"/> written or being written: <see cref="End"/> never moves below this.</summary>
    public long Claimed { get; set; } = done;

    public bool Taken { get; set; }

    /// <summary>A connection has its bytes coming in, as against waiting for its turn or a retry.</summary>
    public bool Receiving { get; set; }

    public long Remaining => End - Start - Claimed;

    public bool IsComplete => Start + Done == End;
}

/// <summary>The file's parts as they were when last saved, for resuming.</summary>
internal sealed record SegmentState(long Start, long End, long Done);

/// <summary>
/// Which connection downloads which bytes. A connection takes a part nobody is downloading; when none is left it
/// takes the second half of the largest part still downloading, so every connection stays busy until the end.
/// </summary>
internal sealed class SegmentPlan
{
    private readonly Lock _lock = new();

    // in order of Start, without gaps or overlaps: a split puts the new part right after the one it came from
    private readonly List<Segment> _segments;
    private readonly long _minSegmentBytes;
    private long _contiguous;
    private TaskCompletionSource _contiguousGrew = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SegmentPlan(IEnumerable<Segment> segments, long minSegmentBytes)
    {
        _segments = [.. segments];
        _minSegmentBytes = Math.Max(1, minSegmentBytes);
        _contiguous = ContiguousFrom(0);
    }

    /// <summary>A new download: <paramref name="connections"/> equal parts, fewer when the file is small.</summary>
    public static SegmentPlan Split(long total, int connections, long minSegmentBytes)
    {
        var count = (int)Math.Clamp(total / Math.Max(1, minSegmentBytes), 1, connections);
        var size = total / count;
        var segments = Enumerable.Range(0, count)
                                 .Select(i => new Segment(i * size, i == count - 1 ? total : (i + 1) * size, 0));
        return new SegmentPlan(segments, minSegmentBytes);
    }

    /// <exception cref="InvalidDataException">the saved parts don't fit the file</exception>
    public static SegmentPlan Resume(long total, IReadOnlyList<SegmentState> saved, long minSegmentBytes)
    {
        var covered = saved.OrderBy(s => s.Start).ToList();
        var position = 0L;
        foreach (var segment in covered)
        {
            if (segment.Start != position || segment.End <= segment.Start || segment.Done < 0 || segment.Done > segment.End - segment.Start)
                throw new InvalidDataException("The saved parts of the download don't fit together.");
            position = segment.End;
        }
        if (position != total)
            throw new InvalidDataException("The saved parts of the download don't cover the file.");
        return new SegmentPlan(covered.Select(s => new Segment(s.Start, s.End, s.Done)), minSegmentBytes);
    }

    /// <summary>The parts saved for <paramref name="target"/>, as long as its part on disk is as they say.</summary>
    /// <exception cref="InvalidDataException">the part isn't the size it was left at, or the saved parts don't fit the file; the message says which</exception>
    public static SegmentPlan Resume(string target, PartState saved, long total, long minSegmentBytes)
    {
        // cut short by something else (a sync tool, a disk that filled): the parts marked done aren't there any more
        var part = new FileInfo(PartFile.PartPath(target));
        if (!part.Exists || part.Length != saved.Total)
            throw new InvalidDataException("The part on disk isn't the size it was left at.");
        return Resume(total, saved.Segments, minSegmentBytes);
    }

    public long Done
    {
        get
        {
            lock (_lock)
                return _segments.Sum(s => s.Done);
        }
    }

    /// <summary>Bytes on disk from the start of the file with no gap, which is what can be hashed so far.</summary>
    public long ContiguousDone
    {
        get
        {
            lock (_lock)
                return _contiguous;
        }
    }

    /// <summary>Connections with bytes coming in right now.</summary>
    public int Active
    {
        get
        {
            lock (_lock)
                return _segments.Count(s => s.Receiving);
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_lock)
                return _segments.All(s => s.IsComplete);
        }
    }

    /// <summary>Completes once <see cref="ContiguousDone"/> is more than <paramref name="than"/>; at once if it already is.</summary>
    public Task ContiguousGrewAsync(long than)
    {
        lock (_lock)
            return _contiguous > than ? Task.CompletedTask : _contiguousGrew.Task;
    }

    /// <summary>A part for a connection to download, or null when there is nothing left worth taking.</summary>
    public Segment? Take()
    {
        lock (_lock)
        {
            if (_segments.FirstOrDefault(s => !s.Taken && s.Remaining > 0) is { } free)
            {
                free.Taken = true;
                return free;
            }
            var largest = _segments.Where(s => s.Taken).MaxBy(s => s.Remaining);
            if (largest is null || largest.Remaining < 2 * _minSegmentBytes)
                return null;
            var middle = largest.Start + largest.Claimed + largest.Remaining / 2;
            var back = new Segment(middle, largest.End, 0) { Taken = true };
            largest.End = middle;
            _segments.Insert(_segments.IndexOf(largest) + 1, back);
            return back;
        }
    }

    /// <summary>The connection stopped working on <paramref name="segment"/>; another may take it.</summary>
    public void Release(Segment segment)
    {
        lock (_lock)
        {
            segment.Taken = false;
            segment.Receiving = false;
            segment.Claimed = segment.Done;
        }
    }

    /// <summary>The connection on <paramref name="segment"/> has its bytes coming in, or has stopped getting them.</summary>
    public void Receiving(Segment segment, bool receiving)
    {
        lock (_lock)
            segment.Receiving = receiving;
    }

    /// <summary>A new try on <paramref name="segment"/>: bytes claimed that never reached the disk are to be fetched again.</summary>
    public void Rewind(Segment segment)
    {
        lock (_lock)
            segment.Claimed = segment.Done;
    }

    /// <summary>Where <paramref name="segment"/> continues, and where it ends now.</summary>
    public (long From, long End) Next(Segment segment)
    {
        lock (_lock)
            return (segment.Start + segment.Claimed, segment.End);
    }

    /// <summary>Reserve up to <paramref name="bytes"/> just received for writing; fewer if the segment was cut short.</summary>
    public int Claim(Segment segment, int bytes)
    {
        lock (_lock)
        {
            var allowed = (int)Math.Min(bytes, segment.Remaining);
            segment.Claimed += allowed;
            return allowed;
        }
    }

    /// <summary>Bytes reserved with <see cref="Claim"/> are on disk.</summary>
    public void Written(Segment segment, int bytes)
    {
        TaskCompletionSource? grew = null;
        lock (_lock)
        {
            var atFront = segment.Start + segment.Done == _contiguous;
            segment.Done += bytes;
            if (atFront)
            {
                _contiguous = ContiguousFrom(_contiguous);
                (grew, _contiguousGrew) = (_contiguousGrew, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }
        grew?.SetResult();
    }

    public long DoneOf(Segment segment)
    {
        lock (_lock)
            return segment.Done;
    }

    public List<SegmentState> Snapshot()
    {
        lock (_lock)
            return [.. _segments.Select(s => new SegmentState(s.Start, s.End, s.Done))];
    }

    /// <summary>How far the bytes on disk reach without a gap, from <paramref name="position"/>, which they reach already.</summary>
    private long ContiguousFrom(long position)
    {
        foreach (var segment in _segments)
        {
            if (segment.End <= position)
                continue;
            position = segment.Start + segment.Done;
            if (!segment.IsComplete)
                break;
        }
        return position;
    }
}
