using Ferry.Core.Http;

namespace Ferry.Core.Sites;

/// <summary>The files behind a link to many.</summary>
/// <param name="Title">what they are together, a folder's name; a folder of its own for them when there is one</param>
/// <param name="Links">a link to each file, as it would be added by hand</param>
public sealed record FileList(string? Title, IReadOnlyList<string> Links);

/// <summary>Knows links that lead to many files: a folder, an album, a directory listing.</summary>
public interface IFileLister
{
    /// <summary>The files behind <paramref name="link"/>; null when it isn't a link of this kind.</summary>
    /// <exception cref="StageException">it is, and they couldn't be read</exception>
    Task<FileList?> TryListAsync(Uri link, RequestDetails details, CancellationToken cancel);
}

/// <summary>Links as they were given, turned into files to add: those that lead to many, listed, and the rest as they are.</summary>
/// <param name="Lists">what the links led to, with the plain links together under no title</param>
/// <param name="Problems">the links that lead to many files, which couldn't be read</param>
public sealed record ExpandedLinks(IReadOnlyList<FileList> Lists, IReadOnlyList<(string Link, string Reason)> Problems);

/// <summary>Turns links to many files into the files, before they are added: a queue only ever sees links to one.</summary>
public sealed class LinkExpander(IReadOnlyList<IFileLister> listers)
{
    public async Task<ExpandedLinks> ExpandAsync(IEnumerable<string> lines, RequestDetails details, CancellationToken cancel)
    {
        var (plain, lists, problems) = (new List<string>(), new List<FileList>(), new List<(string, string)>());
        foreach (var line in lines)
        {
            try
            {
                if (await ListAsync(line, details, cancel) is { } list)
                    lists.Add(list);
                else
                    plain.Add(line);
            }
            catch (StageException e)
            {
                problems.Add((line, e.Message));
            }
        }
        if (plain.Count > 0)
            lists.Insert(0, new FileList(null, plain));
        return new ExpandedLinks(lists, problems);
    }

    private Task<FileList?> ListAsync(string line, RequestDetails details, CancellationToken cancel) =>
        Uri.TryCreate(line.Trim(), UriKind.Absolute, out var link) ? TryListAsync(link, details, cancel) : Task.FromResult<FileList?>(null);

    /// <summary>The files behind <paramref name="link"/>, from the first lister that knows it; null when it is a link to one.</summary>
    /// <exception cref="StageException">it is a link to many, and they couldn't be read</exception>
    public async Task<FileList?> TryListAsync(Uri link, RequestDetails details, CancellationToken cancel)
    {
        foreach (var lister in listers)
        {
            if (await lister.TryListAsync(link, details, cancel) is { } list)
                return list;
        }
        return null;
    }
}
