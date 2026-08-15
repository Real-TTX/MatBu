using MatBu.Services;

namespace MatBu.Tests;

public sealed class SmbPathTests
{
    [Theory]
    [InlineData("\\\\server\\share\\directory")]
    [InlineData("smb://server/share/directory")]
    [InlineData("//server/share/directory")]
    public void ParsesSupportedSyntaxes(string path)
    {
        var result = SmbPath.Parse(path);
        Assert.Equal("server", result.Server);
        Assert.Equal("share", result.ShareName);
        Assert.Equal("directory", result.Directory);
        Assert.Equal("\\\\server\\share\\directory", result.UncPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("server-only")]
    [InlineData("//server/share/../secret")]
    public void RejectsInvalidPaths(string path)
    {
        Assert.Throws<FormatException>(() => SmbPath.Parse(path));
    }
}
