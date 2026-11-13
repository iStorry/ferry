using System.Security.Cryptography;
using System.Text;
using Ferry.Core.Files;

namespace Ferry.Core.Browse;

/// <summary>
/// Which files of a browsed site this PC has downloaded, kept so that nothing about them can be read back. Each is a keyed hash
/// (HMAC-SHA256, 128 bits of it) of the file's slug, one a line in the file; the key is a secret of this install's
/// (<see cref="SecretStore"/>, encrypted for the user on Windows). Without the key the file says nothing: not which
/// files they are, and not whether a given link is among them, as a plain hash of the slug would. Only this install
/// can ask about a file it is shown. Losing the key forgets them all.
/// </summary>
public sealed class DownloadedFiles
{
    public const string KeyName = "downloaded-key";

    private const int MarkBytes = 16;

    private readonly string _path;
    private readonly byte[] _key;
    private readonly Action<string, Exception>? _problem;
    private readonly Lock _lock = new();
    private HashSet<string>? _marks;

    /// <param name="path">the file of marks</param>
    /// <param name="problem">told when the file can't be read or written; the marks are right in memory meanwhile</param>
    /// <exception cref="IOException">the key can't be read or saved</exception>
    public DownloadedFiles(string path, SecretStore secrets, Action<string, Exception>? problem = null)
    {
        (_path, _problem) = (path, problem);
        if (secrets.Load(KeyName) is { Length: > 0 } key)
        {
            _key = key;
            return;
        }
        // a new key can't ask about marks made with another: they go
        _key = RandomNumberGenerator.GetBytes(32);
        secrets.Save(KeyName, _key);
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // they say nothing without their key
            problem?.Invoke("removing the marks of files downloaded under an old key", e);
        }
    }

    /// <summary>The file with this slug was downloaded here.</summary>
    public bool Contains(string slug)
    {
        var mark = Mark(slug);
        lock (_lock)
            return Marks().Contains(mark);
    }

    /// <summary>The file with this slug was downloaded here; added once.</summary>
    public void Add(string slug)
    {
        var mark = Mark(slug);
        lock (_lock)
        {
            if (!Marks().Add(mark))
                return;
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(_path)) is { } folder)
                    Directory.CreateDirectory(folder);
                File.AppendAllText(_path, mark + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // known for this run; added to the file when it is next downloaded
                _problem?.Invoke("marking a file as downloaded", e);
            }
        }
    }

    private string Mark(string slug) => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(slug)).AsSpan(0, MarkBytes));

    /// <summary>The marks, read from the file the first time; one that can't be read starts empty.</summary>
    private HashSet<string> Marks()
    {
        if (_marks is not null)
            return _marks;
        _marks = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path))
                foreach (var line in File.ReadLines(_path))
                {
                    if (line.Length == MarkBytes * 2)
                        _marks.Add(line);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // as if none were downloaded yet, this run
            _problem?.Invoke("reading which files were downloaded", e);
        }
        return _marks;
    }
}
