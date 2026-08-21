using MatBu.Data;

namespace MatBu.Services;

public sealed class BackupScheduler(PersistentStore store, ILogger<BackupScheduler> logger, GeneralSettingsStore generalSettings) : BackgroundService
{
    public const string RetryWaitingState = "Wartet auf Wiederholung";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("MATBU_DOCKER_WORKER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        logger.LogInformation("MatBu scheduler started with time zone {TimeZone}", generalSettings.ResolveTimeZone().Id);
        InitializePersistentSchedules();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { ScheduleDueTasks(); }
            catch (Exception ex) { logger.LogError(ex, "Scheduled task check failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private void InitializePersistentSchedules()
    {
        var now = DateTimeOffset.UtcNow;
        store.Update(data =>
        {
            foreach (var task in data.Tasks)
            {
                task.MaxRetryAttempts = Math.Clamp(task.MaxRetryAttempts, 1, 20);
                task.RetryDelayMinutes = Math.Clamp(task.RetryDelayMinutes, 1, 1440);
                if (!task.Enabled) { task.NextRunDate = null; continue; }
                if (task.NextRunDate is null)
                    task.NextRunDate = BackupSchedule.GetNextOccurrenceUtc(task.Schedule, now, generalSettings.ResolveTimeZone());
            }
        });
    }

    private void ScheduleDueTasks()
    {
        var now = DateTimeOffset.UtcNow;
        store.Update(data =>
        {
            foreach (var task in data.Tasks.Where(task => task.NextRetryDate <= now))
            {
                task.State = "Geplant";
                task.NextRetryDate = null;
                task.UpdateDate = now;
                logger.LogInformation("Task {TaskId} ({TaskName}) retry is due and was queued", task.Id, task.Name);
            }

            foreach (var task in data.Tasks.Where(task => task.Enabled && task.NextRunDate <= now))
            {
                if (task.State == RetryWaitingState)
                {
                    task.NextRunDate = BackupSchedule.GetNextOccurrenceUtc(task.Schedule, now, generalSettings.ResolveTimeZone());
                    continue;
                }

                if (task.State is not ("Geplant" or "Läuft"))
                {
                    task.State = "Geplant";
                    logger.LogInformation("Task {TaskId} ({TaskName}) scheduled for execution", task.Id, task.Name);
                }

                task.NextRunDate = BackupSchedule.GetNextOccurrenceUtc(task.Schedule, now, generalSettings.ResolveTimeZone());
                task.UpdateDate = now;
            }
        });
    }
}
