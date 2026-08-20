using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed class DockerVolumeBackupWorker(PersistentStore store, BackupTaskExecutor executor, ILogger<DockerVolumeBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var isTransferWorker = string.Equals(Environment.GetEnvironmentVariable("MATBU_DOCKER_WORKER"), "true", StringComparison.OrdinalIgnoreCase);
        var isAllInOne = string.Equals(Environment.GetEnvironmentVariable("MATBU_ALL_IN_ONE"), "true", StringComparison.OrdinalIgnoreCase);
        if (!isTransferWorker && !isAllInOne)
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
                    // Bridge the persisted cancel flag (set by the API process on the shared volume) to prompt
                    // in-process cancellation: a 1s watcher fires this job's linked token when a running job of
                    // this task has CancelRequested. Works in both split-container and all-in-one deployments.
                    using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    using var watch = new Timer(_ =>
                    {
                        try
                        {
                            var running = store.Read().TransferJobs
                                .FirstOrDefault(j => j.TaskId == task.Id && j.State == "Running" && j.CancelRequested);
                            if (running is not null) jobCts.Cancel();
                        }
                        catch { /* watcher is best-effort */ }
                    }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                    try { await executor.ExecuteAsync(task, jobCts.Token); }
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

                // A run that was cancelled before the restart must stay cancelled, not silently auto-resume.
                if (job.CancelRequested)
                {
                    job.State = "Abgebrochen";
                    job.Phase = JobPhase.Cancelled;
                    job.CancelRequested = false;
                    job.Error = "";
                    job.UpdateDate = now;
                    if (task is not null)
                    {
                        task.State = "Abgebrochen";
                        task.NextRetryDate = null;
                        task.UpdateDate = now;
                    }
                    data.JobSteps.Add(new JobStep
                    {
                        Id = store.NextId(data.JobSteps.Select(step => step.Id)),
                        TransferJobId = job.Id,
                        Sequence = data.JobSteps.Where(step => step.TransferJobId == job.Id).Select(step => step.Sequence).DefaultIfEmpty().Max() + 1,
                        Stage = "Recovery",
                        State = "Cancelled",
                        Message = "Unterbrochener Lauf hatte einen ausstehenden Abbruch; er wurde endgültig abgebrochen statt fortgesetzt.",
                        InstanceName = "Worker",
                        Location = job.CheckpointPath,
                        BytesTransferred = job.BytesTransferred,
                        TotalBytes = job.TotalBytes,
                        CreateDate = now,
                        UpdateDate = now
                    });
                    continue;
                }

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
