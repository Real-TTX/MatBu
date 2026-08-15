using MatBu.Services;

namespace MatBu.Tests;

public sealed class ProxmoxLocationTests
{
    [Fact]
    public void Parse_ReadsEndpointNodeStorageAndMountedPath()
    {
        var result = ProxmoxLocation.Parse("https://pve.example:8006/?node=pve-01&storage=backup&path=%2Fproxmox-dump&verifyTls=false");

        Assert.Equal("https://pve.example:8006/", result.Endpoint.ToString());
        Assert.Equal("pve-01", result.Node);
        Assert.Equal("backup", result.Storage);
        Assert.Equal("/proxmox-dump", result.ExportPath);
        Assert.False(result.VerifyTls);
    }

    [Fact]
    public void Parse_AllowsNativePbsSourceWithoutDumpMount()
    {
        var result = ProxmoxLocation.Parse("https://pve.example:8006/?node=pve-01&verifyTls=false");

        Assert.Equal("pve-01", result.Node);
        Assert.Equal("", result.Storage);
        Assert.Equal("", result.ExportPath);
    }

    [Theory]
    [InlineData("https://pve.example:8006/")]
    [InlineData("https://pve.example:8006/?node=pve&storage=backup&path=relative")]
    [InlineData("not-a-url")]
    public void Parse_RejectsIncompleteLocations(string value)
    {
        Assert.Throws<FormatException>(() => ProxmoxLocation.Parse(value));
    }
}
