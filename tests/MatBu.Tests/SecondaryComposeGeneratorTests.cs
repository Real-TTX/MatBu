using System.Text.RegularExpressions;
using MatBu.Services;

namespace MatBu.Tests;

public sealed class SecondaryComposeGeneratorTests
{
    [Fact]
    public void GeneratesProductionComposeWithEmbeddedEnrollmentValues()
    {
        var compose = SecondaryComposeGenerator.Generate("https://backup.example.de/", "secret-token");

        Assert.Contains("image: ghcr.io/real-ttx/matbu:latest", compose);
        Assert.Contains("network_mode: bridge", compose);
        Assert.Contains("host.docker.internal:host-gateway", compose);
        Assert.Contains("MATBU_INSTANCE_ROLE: Secondary", compose);
        Assert.Contains("MATBU_PRIMARY_ENDPOINT: \"https://backup.example.de\"", compose);
        Assert.Contains("MATBU_INSTANCE_TOKEN: \"secret-token\"", compose);
        Assert.DoesNotContain("ports:", compose);
        Assert.DoesNotContain("HIER-TOKEN", compose);
    }

    [Fact]
    public void GeneratedTokensAreUrlSafeAndIndependent()
    {
        var first = SecondaryComposeGenerator.GenerateToken();
        var second = SecondaryComposeGenerator.GenerateToken();

        Assert.Matches(new Regex("^[A-Za-z0-9_-]{43}$"), first);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("backup.example.de")]
    [InlineData("ftp://backup.example.de")]
    public void RejectsInvalidPrimaryEndpoint(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => SecondaryComposeGenerator.Generate(endpoint, "token"));
    }
}
