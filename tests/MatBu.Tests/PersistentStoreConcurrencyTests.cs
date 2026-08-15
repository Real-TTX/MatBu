using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MatBu.Tests;

public sealed class PersistentStoreConcurrencyTests
{
    [Fact]
    public void SecondaryCommandPayload_IsProtectedAtRestAndCanBeUnprotected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "matbu-command-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new PersistentStore(new TestHostEnvironment(directory));
            var json = "{\"secret\":\"sensitive-token\"}";
            var protectedValue = store.ProtectSecondaryCommandPayload(json);

            Assert.StartsWith("protected:v1:", protectedValue);
            Assert.DoesNotContain("sensitive-token", protectedValue);
            Assert.Equal(json, store.UnprotectSecondaryCommandPayload(protectedValue));
            Assert.Equal(json, store.UnprotectSecondaryCommandPayload(json));

            var commands = new SecondaryCommandService(store);
            var commandId = commands.Queue(1, SecondaryCommandKind.ObjectTest, "transfer-1", new { Secret = "queued-sensitive-token" });
            var storedPayload = store.Read().SecondaryCommands.Single(command => command.Id == commandId).PayloadJson;
            Assert.StartsWith("protected:v1:", storedPayload);
            Assert.DoesNotContain("queued-sensitive-token", storedPayload);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentFirstStartSeedsDatabaseOnlyOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "matbu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("MATBU_DATA_PATH");
        Environment.SetEnvironmentVariable("MATBU_DATA_PATH", null);
        try
        {
            var environment = new TestHostEnvironment(directory);
            var stores = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => new PersistentStore(environment))));
            var data = stores[0].Read();
            Assert.Single(data.Instances, item => item.Id == 1 && item.Name == "Primary");
            Assert.Single(data.Users, item => item.UserName == "admin");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MATBU_DATA_PATH", previous);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "MatBu.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
