using Ferry.Core.Library;

namespace Ferry.Core.Tests;

/// <summary>The Library: a folder read into titles and their versions, and those matched to what a site lists.</summary>
public sealed class LibraryTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ferry_library_").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("Abc (v0.9.6)", "Abc", "v0.9.6")]
    [InlineData("Abc [Final]", "Abc", "Final")]
    [InlineData("Abc", "Abc", null)]
    public void ANameCarriesItsVersionAtItsEnd(string entry, string name, string? version) =>
        Assert.Equal((name, version), LibraryFolder.Split(entry));

    [Fact]
    public void AFolderIsReadIntoTitlesAndTheirParts()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Abc Two (v2.0)"));
        Directory.CreateDirectory(Path.Combine(_folder, "Abc", "Season 1 (v1.0)"));
        Directory.CreateDirectory(Path.Combine(_folder, "Abc", "Season 2 (P4b)"));
        File.WriteAllText(Path.Combine(_folder, "Other Thing [Final].zip"), "");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "");

        var entries = LibraryFolder.Read(_folder);

        Assert.Equal(["Abc", "Abc Two", "Other Thing"], entries.Select(entry => entry.Name));
        Assert.Equal(["Season 1", "Season 2"], entries[0].Parts.Select(part => part.Name));
        Assert.Equal((false, "Final"), (entries[2].IsFolder, entries[2].Version));
    }

    [Theory]
    [InlineData("Abc", "S2 P4b", "Abc Season 2", "P4b")]
    [InlineData("Abc", "Season 3 v0.1", "Abc Season 3", "v0.1")]
    [InlineData("Abc Season 3", "S3 P4b", "Abc Season 3", "P4b")] // the name says the season already
    [InlineData("Abc", "S2P4b", "Abc", "S2P4b")]                  // not a season and a version: one word
    [InlineData("Abc", "v1.0", "Abc", "v1.0")]
    public void ASeasonAtTheFrontOfAVersionBelongsToTheName(string name, string version, string movedName, string movedVersion) =>
        Assert.Equal((movedName, movedVersion), TitleMatch.MoveSeason(name, version));

    [Fact]
    public void TitlesAreMatchedByNameThenVersion()
    {
        var parts = new[] { Entry("Season 1", "v1.0"), Entry("Season 2", "P4b") };
        var series = Entry("Abc", null, parts);
        List<FoundTitle> found = [new("The Abc Two", "v2.1"), new("Abc Season 2", "P5"), new("Other Thing", "v1.0")];

        Assert.Equal(TitleState.Outdated, TitleMatch.Check(Entry("Abc Two", "v2.0"), null, found));
        Assert.Equal(TitleState.UpToDate, TitleMatch.Check(Entry("Other Thing", "Final"), null, found)); // "Final" is 1.0
        Assert.Equal(TitleState.Unknown, TitleMatch.Check(Entry("Nothing Like It", "v1.0"), null, found));
        Assert.Equal(TitleState.Unknown, TitleMatch.Check(series, null, found));                            // no version of its own
        Assert.Equal(TitleState.Outdated, TitleMatch.Check(series, parts[1], found));
        Assert.True(TitleMatch.Finds(series, found));
    }

    [Fact]
    public void VersionsAreTheSameWhateverIsAroundTheirNumber()
    {
        Assert.True(TitleMatch.SameVersion("v0.9.6", "0.9.6"));
        Assert.True(TitleMatch.SameVersion("v0.9.6 Reworked", "V0.9.6"));
        Assert.True(TitleMatch.SameVersion("vP4b", "P4b"));
        Assert.False(TitleMatch.SameVersion("v0.9.6", "v0.9.7"));
    }

    [Fact]
    public void ANameIsSearchedShorterUntilSomethingIsFound() =>
        Assert.Equal(["The Quiet Harbor Pro", "Quiet Harbor Pro", "The Quiet Harbor", "Quiet"], TitleMatch.Searches("The Quiet Harbor Pro"));

    private static LibraryEntry Entry(string name, string? version, IReadOnlyList<LibraryEntry>? parts = null) =>
        new(name, version, Path.Combine("library", name), IsFolder: true, DateTime.UnixEpoch, parts ?? []);
}
