using MatBu.Services;

namespace MatBu.Tests;

public sealed class SourceSelectionTests
{
    [Fact]
    public void NormalizeRemovesTraversalDuplicatesAndChildren()
    {
        var result = SourceSelection.Normalize(["Kunden/Alpha", "Kunden\\Alpha\\Daten", "../secret", "Kunden/Beta", "kunden/alpha"]);
        Assert.Equal(["Kunden/Alpha", "Kunden/Beta"], result);
    }

    [Theory]
    [InlineData("Kunden/Alpha/datei.vmdk", true)]
    [InlineData("Kunden/Beta/datei.vmdk", false)]
    public void IncludesOnlySelectedTree(string path, bool expected)
    {
        Assert.Equal(expected, SourceSelection.Includes(path, ["Kunden/Alpha"]));
    }
}
