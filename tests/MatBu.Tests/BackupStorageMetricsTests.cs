using MatBu.Models;
using MatBu.Services;

namespace MatBu.Tests;

public sealed class BackupStorageMetricsTests
{
    [Fact]
    public void FullBackupsReportLatestPlainSizeAndAllVersions()
    {
        var data = new AppData();
        data.TransferJobs.AddRange(
            CompletedJob(1, 10, 100, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            CompletedJob(2, 10, 120, new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)));

        var metrics = BackupStorageMetricsCalculator.Calculate(data, 10);

        Assert.Equal(120, metrics.CurrentBytes);
        Assert.Equal(220, metrics.TotalWithVersionsBytes);
        Assert.Equal(2, metrics.VersionCount);
    }

    [Fact]
    public void ExpiredBackupsAreExcluded()
    {
        var data = new AppData();
        var expired = CompletedJob(1, 10, 100, DateTimeOffset.UtcNow);
        expired.RetentionExpired = true;
        data.TransferJobs.Add(expired);

        Assert.False(BackupStorageMetricsCalculator.Calculate(data, 10).HasBackup);
    }

    private static TransferJob CompletedJob(long id, long taskId, long bytes, DateTimeOffset created) => new()
    {
        Id = id,
        TaskId = taskId,
        State = "Completed",
        SourceBytes = bytes,
        TotalBytes = bytes,
        ResolvedDestination = $"/backup/{id}",
        CreateDate = created,
        UpdateDate = created
    };
}
