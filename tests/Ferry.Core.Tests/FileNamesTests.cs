using System.Text;
using Ferry.Core.Files;

namespace Ferry.Core.Tests;

public sealed class FileNamesTests
{
    [Theory]
    [InlineData("Tom & Jerry: Part 1.mp4", "Tom & Jerry_ Part 1.mp4")]
    [InlineData("a/b\\c|d?e*f\"g<h>i.zip", "a_b_c_d_e_f_g_h_i.zip")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    [InlineData("name. . .", "name")]
    [InlineData("CON.mp4", "_CON.mp4")]
    [InlineData("nul", "_nul")]
    [InlineData("console.mp4", "console.mp4")]
    [InlineData("   ", FileNames.Fallback)]
    [InlineData("...", FileNames.Fallback)]
    public void NamesAreSafeOnEveryOs(string name, string expected) => Assert.Equal(expected, FileNames.Sanitize(name));

    [Fact]
    public void NamesCantPretendToBeSomethingElse()
    {
        // a right-to-left override would show "x\u202Egpj.exe" as "xexe.jpg"; controls of any kind go too
        Assert.Equal("xgpj.exe", FileNames.Sanitize("x\u202Egpj.exe"));
        Assert.Equal("a_b_c.mp4", FileNames.Sanitize("a\u009Bb\u007Fc.mp4"));
    }

    [Fact]
    public void LongNamesKeepTheirExtensionAndWholeCharacters()
    {
        var name = string.Concat(Enumerable.Repeat("日本語👍🏽", 40)) + ".mkv";
        var safe = FileNames.Sanitize(name);
        Assert.EndsWith(".mkv", safe, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(safe) <= FileNames.MaxBytes);
        Assert.StartsWith(safe[..^4], name, StringComparison.Ordinal);
        Assert.DoesNotContain('�', Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(safe)));
        Assert.False(char.IsHighSurrogate(safe[^5]));
    }
}
