using MatBu.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MatBu.Tests;

public sealed class PersistentStoreConcurrencyTests
{
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
