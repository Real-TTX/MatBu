using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed class DockerVolumeBackupWorker(PersistentStore store, BackupTaskExecutor executor, ILogger<DockerVolumeBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MATBU_DOCKER_WORKER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        logger.LogInformation("MatBu transfer worker started");
        RecoverInterruptedJobs();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tasks = store.Read().Tasks.Where(task => task.State == "Geplant").ToList();
                foreach (var task in tasks)
                {
                    try { await executor.ExecuteAsync(task, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogError(ex, "Task {TaskId} execution failed", task.Id); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Transfer worker cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private void RecoverInterruptedJobs()
    {
        store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var job in data.TransferJobs.Where(job => job.State == "Running"))
            {
                var task = data.Tasks.FirstOrDefault(task => task.Id == job.TaskId);
                job.State = "Fehler";
                job.Error = task is null
                    ? "Worker wurde neu gestartet; die zugehörige Job-Konfiguration wurde inzwischen gelöscht und der Lauf kann nicht fortgesetzt werden."
                    : "Worker wurde neu gestartet; der Lauf wird automatisch vom vorhandenen Checkpoint fortgesetzt.";
                job.UpdateDate = now;

                if (task is not null)
                {
                    task.State = "Geplant";
                    task.NextRetryDate = null;
                    task.UpdateDate = now;
                }

                data.JobSteps.Add(new JobStep
                {
                    Id = store.NextId(data.JobSteps.Select(step => step.Id)),
                    TransferJobId = job.Id,
                    Sequence = data.JobSteps.Where(step => step.TransferJobId == job.Id).Select(step => step.Sequence).DefaultIfEmpty().Max() + 1,
                    Stage = "Recovery",
                    State = task is null ? "Failed" : "Resumed",
                    Message = task is null
                        ? "Unterbrochener Lauf erkannt; die zugehörige Job-Konfiguration existiert nicht mehr und der Lauf wurde endgültig beendet."
                        : "Unterbrochener Lauf erkannt und automatisch zur Wiederaufnahme eingeplant.",
                    InstanceName = "Worker",
                    Location = job.CheckpointPath,
                    BytesTransferred = job.BytesTransferred,
                    TotalBytes = job.TotalBytes,
                    CreateDate = now,
                    UpdateDate = now
                });
            }
        });
    }
}
