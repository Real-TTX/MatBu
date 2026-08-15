using MatBu.Models;

namespace MatBu.Services;

public sealed record BackupStorageMetrics(long CurrentBytes, long TotalWithVersionsBytes, int VersionCount)
{
    public long VersionBytes => Math.Max(0, TotalWithVersionsBytes - CurrentBytes);
    public bool HasBackup => VersionCount > 0;
}

public static class BackupStorageMetricsCalculator
{
    public static IReadOnlyDictionary<long, BackupStorageMetrics> CalculateAll(AppData data) =>
        data.Tasks.ToDictionary(task => task.Id, task => Calculate(data, task.Id, task.Token));

    public static BackupStorageMetrics Calculate(AppData data, long taskId, string? taskToken = null)
    {
        var jobs = data.TransferJobs
            .Where(job => IsBackupVersion(job, taskId, taskToken))
            .OrderBy(job => job.CreateDate)
            .ToList();
        if (jobs.Count == 0) return new BackupStorageMetrics(0, 0, 0);

        var jobIds = jobs.Select(job => job.Id).ToHashSet();
        var snapshots = data.BackupSnapshots
            .Where(snapshot => snapshot.TaskId == taskId && snapshot.State.Equals("Completed", StringComparison.OrdinalIgnoreCase) && jobIds.Contains(snapshot.TransferJobId))
            .OrderBy(snapshot => snapshot.CreateDate)
            .ToList();
        var snapshotsByJob = snapshots.ToDictionary(snapshot => snapshot.TransferJobId);

        var latestJob = jobs[^1];
        var currentBytes = snapshotsByJob.TryGetValue(latestJob.Id, out var latestSnapshot)
            ? latestSnapshot.TotalBytes
            : JobSize(latestJob);

        // Reverse Incremental speichert beim ersten Lauf den Current-Stand und danach nur
        // die neuen/ersetzten Blöcke. Die Summe der gespeicherten Snapshot-Blöcke bildet
        // damit den belegten Datenbestand inklusive Versionen ab (Metadaten ausgenommen).
        var reverseBytes = snapshots.Sum(snapshot => Math.Max(0, snapshot.StoredBytes));
        var fullBytes = jobs
            .Where(job => !snapshotsByJob.ContainsKey(job.Id))
            .Sum(JobSize);
        var totalBytes = Math.Max(currentBytes, reverseBytes + fullBytes);
        return new BackupStorageMetrics(currentBytes, totalBytes, jobs.Count);
    }

    public static long VersionSize(AppData data, TransferJob job)
    {
        var snapshot = data.BackupSnapshots.FirstOrDefault(item => item.Id == job.SnapshotId || item.TransferJobId == job.Id);
        return snapshot?.TotalBytes ?? JobSize(job);
    }

    private static bool IsBackupVersion(TransferJob job, long taskId, string? taskToken) =>
        job.TaskId == taskId &&
        !job.RetentionExpired &&
        (string.IsNullOrWhiteSpace(taskToken) || string.IsNullOrWhiteSpace(job.TaskToken) || job.TaskToken.Equals(taskToken, StringComparison.Ordinal)) &&
        job.State.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
        !job.SourceObjectKind.Equals("BackupVersion", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(job.ResolvedDestination);

    private static long JobSize(TransferJob job) => Math.Max(0,
        job.SourceBytes > 0 ? job.SourceBytes :
        job.TotalBytes > 0 ? job.TotalBytes :
        job.BytesTransferred);
}
