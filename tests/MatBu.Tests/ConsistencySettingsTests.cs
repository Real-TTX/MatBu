using MatBu.Models;
using MatBu.Services;

namespace MatBu.Tests;

public sealed class ConsistencySettingsTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(60, 60)]
    [InlineData(1000, 900)]
    public void ClampsDockerTimeout(int configured, int expected)
    {
        var settings = BackupConsistencySettings.FromTask(new BackupTask { ConsistencyTimeoutSeconds = configured });
        Assert.Equal(expected, settings.TimeoutSeconds);
    }

    [Theory]
    [InlineData(null, 120)]
    [InlineData("1", 5)]
    [InlineData("45", 45)]
    [InlineData("9999", 3600)]
    public void ResolvesSecondaryIdleTimeout(string? configured, int expectedSeconds)
    {
        var previous = Environment.GetEnvironmentVariable("MATBU_SECONDARY_COMMAND_IDLE_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("MATBU_SECONDARY_COMMAND_IDLE_TIMEOUT_SECONDS", configured);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SecondaryCommandService.ResolveInactivityTimeout());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MATBU_SECONDARY_COMMAND_IDLE_TIMEOUT_SECONDS", previous);
        }
    }
}
