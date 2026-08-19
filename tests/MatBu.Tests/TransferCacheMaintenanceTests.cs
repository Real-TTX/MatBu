using MatBu.Services;

namespace MatBu.Tests;

public sealed class TransferCacheMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"matbu-cache-test-{Guid.NewGuid():N}");

    [Fact]
    public void CleanupExpiredFiles_DeletesOnlyExpiredCacheFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "incremental", "old"));
        var oldFile = Path.Combine(_root, "incremental", "old", "checkpoint.partial");
        var currentFile = Path.Combine(_root, "current.tar");
        File.WriteAllBytes(oldFile, new byte[4096]);
        File.WriteAllBytes(currentFile, new byte[2048]);
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-8));

        var result = TransferCacheMaintenanceService.CleanupExpiredFiles(_root, DateTime.UtcNow.AddDays(-7));

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(4096, result.ReclaimedBytes);
        Assert.False(File.Exists(oldFile));
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldFile)));
        Assert.True(File.Exists(currentFile));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
