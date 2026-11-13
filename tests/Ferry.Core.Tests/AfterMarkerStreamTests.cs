using System.Text;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>What a server's shell startup files print before a command runs is not the command's output.</summary>
public sealed class AfterMarkerStreamTests
{
    private const string Line = AfterMarkerStream.Marker + "\n";

    private static async Task<string> ReadAllAsync(string written, int chunk = 3)
    {
        await using var stream = new AfterMarkerStream(new MemoryStream(Encoding.UTF8.GetBytes(written)));
        using var read = new MemoryStream();
        var buffer = new byte[chunk];
        int count;
        while ((count = await stream.ReadAsync(buffer)) > 0)
            read.Write(buffer, 0, count);
        return Encoding.UTF8.GetString(read.ToArray());
    }

    [Theory]
    [InlineData("", "the file")]
    [InlineData("Welcome to the box\n", "the file")]
    [InlineData("no newline at the end of this", "the file")]
    [InlineData("", "")]
    public async Task OnlyWhatComesAfterTheMarkerIsRead(string noise, string output)
    {
        Assert.Equal(output, await ReadAllAsync(noise + Line + output));
        Assert.Equal(output, AfterMarkerStream.TextAfter(noise + Line + output));
    }

    [Fact]
    public async Task WithNoMarkerWhatCameIsPassedOn()
    {
        Assert.Equal("just this", await ReadAllAsync("just this"));
        Assert.Equal("just this", AfterMarkerStream.TextAfter("just this"));
    }

    [Fact]
    public async Task TheMarkerAgainInTheFileIsTheFiles() =>
        Assert.Equal("a" + Line + "b", await ReadAllAsync("noise" + Line + "a" + Line + "b"));

    [Fact]
    public void EveryCommandIsRunBySh()
    {
        var command = ServerConnection.OnServer("echo 'hi'");

        Assert.StartsWith("sh -c '", command, StringComparison.Ordinal);
        Assert.Contains(AfterMarkerStream.Marker, command, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', command);
    }
}
