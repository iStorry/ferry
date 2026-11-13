using System.Text;
using Ferry.Core.Files;

namespace Ferry.Core.Tests;

/// <summary>Replacing a file whole: the content, the temporary file, and the way the file is opened.</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ferry_atomic_").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string PathOf(string name) => Path.Combine(_folder, name);

    [Fact]
    public void TheNewContentReplacesTheOldAndLeavesNoTemporaryFile()
    {
        var path = PathOf("list.json");
        AtomicFile.WriteText(path, "[1]");

        AtomicFile.WriteText(path, "[1, 2]");

        Assert.Equal("[1, 2]", File.ReadAllText(path));
        Assert.Equal(["list.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void TextIsWrittenAsUtf8WithoutAMark()
    {
        var path = PathOf("name.txt");

        AtomicFile.WriteText(path, "ñ");

        Assert.Equal("ñ"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void TheTemporaryFileIsOpenedTheCallersWay()
    {
        var path = PathOf("secret");
        string? opened = null;

        AtomicFile.Write(path, Encoding.UTF8.GetBytes("hush"), temporary =>
        {
            opened = temporary;
            return new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
        });

        Assert.Equal(path + ".tmp", opened);
        Assert.Equal("hush", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void DeletingTakesALeftoverTemporaryFileWithIt()
    {
        var path = PathOf("state.json");
        File.WriteAllText(path, "{}");
        File.WriteAllText(path + ".tmp", "{"); // a write cut short

        AtomicFile.Delete(path);

        Assert.Empty(Directory.GetFiles(_folder));
    }
}
