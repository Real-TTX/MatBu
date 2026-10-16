using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

/// <summary>The result of the most recent finished backup run.</summary>
public enum BackupRunResult { None, Succeeded, Failed, Cancelled }

/// <summary>An overview of the installation: what the dashboard, the platform tiles and the health check show.</summary>
public sealed record MatBuStatus(
    int Tasks,
    int ActiveTasks,
    int RunningJobs,
    DateTimeOffset? LastSuccessfulBackup,
    BackupRunResult LastResult,
    string LastResultTask,
    DateTimeOffset? LastResultDate,
    DateTimeOffset? NextRun,
    long StorageBytes,
    int FailedJobs24Hours,
    int FailingTasks,
    int Objects,
    int ObjectsNeedingAttention,
    int SecondariesOffline);

/// <summary>What <see cref="MatBuStatusService.QueueAllBackups"/> did.</summary>
public sealed record QueueBackupsResult(int Queued, int AlreadyQueued);

/// <summary>
/// Reads the state of MatBu for the overview of the platform contract (tiles and health) and starts all backups at once.
/// The overview is cached for a few seconds, so <c>/api/health</c> stays cheap when it is called every few seconds.
/// </summary>
public sealed class MatBuStatusService(PersistentStore store, ILogger<MatBuStatusService> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SecondaryOnlineWindow = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private MatBuStatus? _cached;
    private DateTimeOffset _cachedAt;

    /// <summary>The overview, at most a few seconds old.</summary>
    public MatBuStatus GetStatus()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cached is not null && now - _cachedAt < CacheLifetime) return _cached;
            _cached = Calculate(store.ReadOverview(), now);
            _cachedAt = now;
            return _cached;
        }
    }

    /// <summary>True when the database can be read.</summary>
    public bool CanReadDatabase()
    {
        try
        {
            GetStatus();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "The MatBu database cannot be read");
            return false;
        }
    }

    /// <summary>
    /// Queues every enabled task for an immediate run (like "Jetzt ausführen" on a task); a task that is already queued or running stays as it is.
    /// The scheduler and the worker pick the tasks up as usual.
    /// </summary>
    public QueueBackupsResult QueueAllBackups()
    {
        var queued = 0;
        var already = 0;
        store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var task in data.Tasks.Where(task => task.Enabled))
            {
                if (task.State is "Geplant" or "Läuft")
                {
                    already++;
                    continue;
                }

                task.State = "Geplant";
                task.NextRetryDate = null;
                task.UpdateDate = now;
                queued++;
            }
        });
        Invalidate();
        return new QueueBackupsResult(queued, already);
    }

    private void Invalidate()
    {
        lock (_gate) _cached = null;
    }

    /// <summary>The overview of <paramref name="data"/> at <paramref name="now"/> (public for the tests).</summary>
    public static MatBuStatus Calculate(AppData data, DateTimeOffset now)
    {
        var cutoff = now.AddHours(-24);
        var backupRuns = data.TransferJobs.Where(IsBackupRun).ToList();
        var lastFinished = backupRuns
            .Where(job => job.State is "Completed" or "Fehler" or "Abgebrochen")
            .OrderByDescending(job => job.UpdateDate)
            .FirstOrDefault();
        var lastResult = lastFinished?.State switch
        {
            "Completed" => BackupRunResult.Succeeded,
            "Fehler" => BackupRunResult.Failed,
            "Abgebrochen" => BackupRunResult.Cancelled,
            _ => BackupRunResult.None
        };
        var lastSuccess = backupRuns
            .Where(job => job.State == "Completed" && !string.IsNullOrWhiteSpace(job.ResolvedDestination))
            .Select(job => (DateTimeOffset?)job.UpdateDate)
            .DefaultIfEmpty()
            .Max();
        var nextRun = data.Tasks
            .Where(task => task.Enabled)
            .Select(task => task.NextRetryDate is { } retry && (task.NextRunDate is null || retry < task.NextRunDate) ? retry : task.NextRunDate)
            .Where(date => date is not null)
            .DefaultIfEmpty()
            .Min();
        var storage = BackupStorageMetricsCalculator.CalculateAll(data).Values.Sum(metrics => metrics.TotalWithVersionsBytes);
        var onlineCutoff = now - SecondaryOnlineWindow;

        return new MatBuStatus(
            Tasks: data.Tasks.Count,
            ActiveTasks: data.Tasks.Count(task => task.Enabled),
            RunningJobs: data.TransferJobs.Count(job => job.State == "Running"),
            LastSuccessfulBackup: lastSuccess,
            LastResult: lastResult,
            LastResultTask: lastFinished?.TaskName ?? "",
            LastResultDate: lastFinished?.UpdateDate,
            NextRun: nextRun,
            StorageBytes: storage,
            FailedJobs24Hours: data.TransferJobs.Count(job => job.State == "Fehler" && job.UpdateDate >= cutoff),
            FailingTasks: data.Tasks.Count(task => task.State == "Fehler"),
            Objects: data.Objects.Count,
            ObjectsNeedingAttention: data.Objects.Count(item => item.Status != ObjectStatus.Healthy),
            SecondariesOffline: data.Instances.Count(instance =>
                instance.Role == InstanceRole.Secondary && instance.Enabled &&
                (instance.Status != InstanceStatus.Online || instance.LastSeenDate is null || instance.LastSeenDate < onlineCutoff)));
    }

    /// <summary>A run of a backup task (a restore reads from a backup version and is no backup).</summary>
    private static bool IsBackupRun(TransferJob job) =>
        job.TaskId > 0 &&
        !job.SourceObjectKind.Equals("BackupVersion", StringComparison.OrdinalIgnoreCase);
}
