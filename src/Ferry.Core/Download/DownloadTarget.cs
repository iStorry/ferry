using System.Diagnostics;
using Ferry.Core.Files;

namespace Ferry.Core.Download;

/// <summary>The file a download (or a transfer from the server) goes to, and the earlier attempt at it to continue.</summary>
internal sealed record DownloadTarget(string Path, PartState? Saved = null, bool Overwrite = false, bool Skip = false)
{
    // names chosen by downloads running in this process, until they finish: two files of the same name starting
    // together would otherwise both see the name free, since a part file appears only after the first answer
    private static readonly HashSet<string> Taken = new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    private static readonly Lock TakenLock = new();

    /// <summary>
    /// The first of "name.ext", "name (1).ext", ... that is this file's unfinished download, or is free (no file, no
    /// other download's part, not chosen by a download running now). An existing "name.ext" is skipped or overwritten
    /// instead when chosen. Unless skipped, the name is held until <see cref="Release"/>.
    /// </summary>
    /// <param name="key">the file's key on its site, which identifies its unfinished download</param>
    public static DownloadTarget Choose(string wanted, string key, ExistingFile onExisting)
    {
        lock (TakenLock)
        {
            foreach (var candidate in FileNames.Candidates(wanted))
            {
                if (Taken.Contains(candidate))
                    continue;
                // its state was cut short by a crash: the part can't be trusted, and the name is free again
                if (PartFile.IsDamaged(candidate) && !PartFile.TryDelete(candidate))
                    continue;
                if (PartFile.Load(candidate) is { } saved && saved.Slug == key)
                    return Hold(new DownloadTarget(candidate, saved, Overwrite: candidate == wanted && onExisting == ExistingFile.Overwrite));
                var first = candidate == wanted;
                if (IsThere(candidate))
                {
                    if (first && onExisting == ExistingFile.Skip)
                        return new DownloadTarget(candidate, Skip: true);
                    if (first && onExisting == ExistingFile.Overwrite && !PartFile.PartExists(candidate))
                        return Hold(new DownloadTarget(candidate, Overwrite: true));
                    continue;
                }
                if (!PartFile.PartExists(candidate))
                    return Hold(new DownloadTarget(candidate));
            }
        }
        throw new UnreachableException("FileNames.Candidates never runs out.");
    }

    /// <summary>The download using <paramref name="path"/> has ended (finished, failed or stopped).</summary>
    public static void Release(string path)
    {
        lock (TakenLock)
            Taken.Remove(path);
    }

    /// <summary>
    /// Give the complete part its final name and remove its state; returns that name. If a file of the same name
    /// appeared while it downloaded (and it isn't to be overwritten), the next free name is used instead. A part that
    /// is a folder of files is never put over anything, as what is there would be mixed in with it.
    /// </summary>
    /// <param name="ending">what to add to the name, when what the file is only showed by the end</param>
    public string Complete(string? ending = null)
    {
        var (final, part) = (Path + ending, PartFile.PartPath(Path));
        var isFolder = Directory.Exists(part);
        lock (TakenLock)
        {
            if ((!Overwrite || isFolder) && IsThere(final))
            {
                final = FileNames.Candidates(final).First(c => !Taken.Contains(c) && !IsThere(c) && !PartFile.PartExists(c));
                Taken.Add(final);
            }
        }
        try
        {
            if (isFolder)
                Directory.Move(part, final);
            else
                File.Move(part, final, overwrite: Overwrite);
            PartFile.TryDelete(Path); // the file is finished: a state file held for a moment doesn't change that
        }
        finally
        {
            if (final != Path)
                Release(final);
        }
        return final;
    }

    private static bool IsThere(string path) => File.Exists(path) || Directory.Exists(path);

    private static DownloadTarget Hold(DownloadTarget target)
    {
        Taken.Add(target.Path);
        return target;
    }
}
