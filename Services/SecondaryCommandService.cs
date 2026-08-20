using System.Text.Json;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed record SecondaryCommandEnvelope(long Id, SecondaryCommandKind Kind, string TransferId, string PayloadJson);
public sealed record SecondaryCommandProgress(
    long BytesTransferred,
    long TotalBytes,
    long SpeedBytesPerSecond,
    string? Checkpoint,
    long BytesRead = 0,
    long BytesWritten = 0,
    long ReadSpeedBytesPerSecond = 0,
    long WriteSpeedBytesPerSecond = 0,
    string? Phase = null,
    long EstimatedSourceBytes = 0,
    long EstimatedStoredBytes = 0);

public sealed class SecondaryCommandService(PersistentStore store)
{
    public long Queue(long instanceId, SecondaryCommandKind kind, string transferId, object payload)
    {
        long id = 0;
        store.Update(data =>
        {
            id = store.NextId(data.SecondaryCommands.Select(x => x.Id));
            var now = DateTimeOffset.UtcNow;
            data.SecondaryCommands.Add(new SecondaryCommand
            {
                Id = id,
                InstanceId = instanceId,
                Kind = kind,
                TransferId = transferId,
                PayloadJson = store.ProtectSecondaryCommandPayload(JsonSerializer.Serialize(payload)),
                State = "Queued",
                CreateDate = now,
                UpdateDate = now
            });
        });
        return id;
    }

    public MatBuInstance? FindInstance(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var data = store.Read();
        foreach (var instance in data.Instances.Where(x => x.Role == InstanceRole.Secondary && x.Enabled))
        {
            if (string.Equals(store.GetInstanceToken(instance.Id), token, StringComparison.Ordinal)) return instance;
        }
        return null;
    }

    public SecondaryCommandEnvelope? LeaseNext(string token)
    {
        var instance = FindInstance(token);
        if (instance is null) return null;

        var now = DateTimeOffset.UtcNow;
        var snapshot = store.Read();
        var hasStaleCommand = snapshot.SecondaryCommands.Any(x => x.State == "Running" && x.UpdateDate < now.AddSeconds(-30));
        var hasQueuedCommand = snapshot.SecondaryCommands.Any(x => x.InstanceId == instance.Id && x.State == "Queued");
        var heartbeatDue = instance.LastSeenDate is null || now - instance.LastSeenDate.Value >= TimeSpan.FromSeconds(15);
        if (!hasStaleCommand && !hasQueuedCommand && !heartbeatDue) return null;

        SecondaryCommand? leased = null;
        store.Update(data =>
        {
            var currentInstance = data.Instances.FirstOrDefault(x => x.Id == instance.Id);
            if (currentInstance is null) return;
            currentInstance.LastSeenDate = now;
            currentInstance.Status = InstanceStatus.Online;
            currentInstance.LastMessage = "Ausgehende Verbindung zur Primary aktiv.";
            currentInstance.UpdateDate = now;

            var staleBefore = now.AddSeconds(-30);
            // Do not resurrect a cancel-requested command via the stale-requeue sweep.
            foreach (var stale in data.SecondaryCommands.Where(x => x.State == "Running" && !x.CancelRequested && x.UpdateDate < staleBefore)) stale.State = "Queued";
            // A queued command that was cancelled before it ever started is finalized without running.
            foreach (var cancelledQueued in data.SecondaryCommands.Where(x => x.InstanceId == instance.Id && x.State == "Queued" && x.CancelRequested))
            {
                cancelledQueued.State = "Cancelled";
                cancelledQueued.UpdateDate = now;
            }
            leased = data.SecondaryCommands.Where(x => x.InstanceId == instance.Id && x.State == "Queued" && !x.CancelRequested).OrderBy(x => x.Id).FirstOrDefault();
            if (leased is null) return;
            var current = data.SecondaryCommands.First(x => x.Id == leased.Id);
            current.State = "Running";
            current.UpdateDate = now;
        });
        return leased is null ? null : new SecondaryCommandEnvelope(leased.Id, leased.Kind, leased.TransferId, store.UnprotectSecondaryCommandPayload(leased.PayloadJson));
    }

    public SecondaryCommand? Get(long id) => store.Read().SecondaryCommands.FirstOrDefault(x => x.Id == id);

    public Task<SecondaryCommand> WaitForCompletionAsync(long commandId, CancellationToken cancellationToken) =>
        WaitForCompletionAsync(commandId, ResolveInactivityTimeout(), cancellationToken);

    public async Task<SecondaryCommand> WaitForCompletionAsync(long commandId, TimeSpan inactivityTimeout, CancellationToken cancellationToken)
    {
        if (inactivityTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        DateTimeOffset? missingSince = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = Get(commandId);
            if (command is null)
            {
                missingSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - missingSince > TimeSpan.FromSeconds(30)) throw new InvalidOperationException($"Secondary-Kommando {commandId} wurde nicht gefunden.");
            }
            else
            {
                missingSince = null;
                if (command.State is "Completed" or "Failed") return command;
                // A user-cancelled command is terminal; surface it as cancellation so the primary unwinds
                // its own transfer instead of waiting for the idle timeout.
                if (command.State == "Cancelled")
                    throw new OperationCanceledException($"Secondary-Kommando {commandId} wurde abgebrochen.");
                if (DateTimeOffset.UtcNow - command.UpdateDate > inactivityTimeout)
                    throw new TimeoutException($"Secondary-Kommando {commandId} hat seit {inactivityTimeout.TotalSeconds:0} Sekunden keinen Fortschritt gemeldet.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    public static TimeSpan ResolveInactivityTimeout()
    {
        var configured = Environment.GetEnvironmentVariable("MATBU_SECONDARY_COMMAND_IDLE_TIMEOUT_SECONDS");
        return int.TryParse(configured, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 3600))
            : TimeSpan.FromMinutes(2);
    }

    public bool IsCancelRequested(long id) => Get(id)?.CancelRequested == true;

    public bool Complete(string token, long commandId, bool success, string resultJson, string error)
    {
        if (FindInstance(token) is null) return false;
        var changed = false;
        store.Update(data =>
        {
            var command = data.SecondaryCommands.FirstOrDefault(x => x.Id == commandId);
            if (command is null) return;
            // Keep an already-cancelled command terminal, and map a failure caused by a cancel to "Cancelled"
            // rather than "Failed" so it is not treated as an error downstream.
            if (command.State == "Cancelled") { changed = true; return; }
            command.State = success ? "Completed" : (command.CancelRequested ? "Cancelled" : "Failed");
            command.ResultJson = resultJson ?? "";
            command.Error = error ?? "";
            command.UpdateDate = DateTimeOffset.UtcNow;
            changed = true;
        });
        return changed;
    }

    public bool UpdateProgress(string token, long commandId, SecondaryCommandProgress progress)
    {
        if (FindInstance(token) is null) return false;
        var changed = false;
        store.Update(data =>
        {
            var command = data.SecondaryCommands.FirstOrDefault(x => x.Id == commandId);
            if (command is null) return;
            command.BytesTransferred = progress.BytesTransferred;
            command.TotalBytes = progress.TotalBytes;
            command.SpeedBytesPerSecond = progress.SpeedBytesPerSecond;
            command.UpdateDate = DateTimeOffset.UtcNow;
            var job = data.TransferJobs.FirstOrDefault(item => item.TransferId == command.TransferId && item.State == "Running");
            if (job is not null)
            {
                job.BytesRead = Math.Max(job.BytesRead, progress.BytesRead);
                job.BytesTransferred = Math.Max(job.BytesTransferred, progress.BytesTransferred);
                job.BytesWritten = Math.Max(job.BytesWritten, progress.BytesWritten);
                if (progress.TotalBytes > 0) job.TotalBytes = progress.TotalBytes;
                job.ReadSpeedBytesPerSecond = progress.ReadSpeedBytesPerSecond;
                job.SpeedBytesPerSecond = progress.SpeedBytesPerSecond;
                if (progress.WriteSpeedBytesPerSecond > 0 || progress.BytesWritten > 0)
                    job.WriteSpeedBytesPerSecond = progress.WriteSpeedBytesPerSecond;
                if (!string.IsNullOrEmpty(progress.Phase)) job.Phase = progress.Phase;
                if (progress.EstimatedSourceBytes > 0) job.EstimatedSourceBytes = progress.EstimatedSourceBytes;
                if (progress.EstimatedStoredBytes > 0) job.EstimatedStoredBytes = progress.EstimatedStoredBytes;
                job.CheckpointPath = progress.Checkpoint ?? job.CheckpointPath;
                job.UpdateDate = DateTimeOffset.UtcNow;
            }
            changed = true;
        });
        return changed;
    }
}
