using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed class NotificationDispatcher(
    PersistentStore store,
    NotificationSettingsStore settingsStore,
    NotificationService notificationService,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("MATBU_INSTANCE_ROLE"), "Secondary", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("MATBU_DOCKER_WORKER"), "true", StringComparison.OrdinalIgnoreCase)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchAsync(stoppingToken); }
            catch (Exception exception) { logger.LogError(exception, "Notification dispatch failed."); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        var settings = settingsStore.Read();
        var channels = EnabledChannels(settings);
        if (channels.Count == 0 || (!settings.NotifyOnSuccess && !settings.NotifyOnFailure)) return;

        RegisterDeliveries(settings, channels);
        var due = store.Read().NotificationDeliveries
            .Where(item => item.State != "Sent" && item.Attempt < 3 && (item.NextAttemptDate is null || item.NextAttemptDate <= DateTimeOffset.UtcNow))
            .OrderBy(item => item.Id)
            .Take(10)
            .ToList();

        foreach (var delivery in due)
        {
            var job = store.Read().TransferJobs.FirstOrDefault(item => item.Id == delivery.TransferJobId);
            if (job is null) { UpdateDelivery(delivery.Id, new(false, "Job wurde nicht gefunden.")); continue; }
            var result = delivery.Channel == "Webhook"
                ? await notificationService.SendJobWebhookAsync(settings, job, cancellationToken)
                : await notificationService.SendJobEmailAsync(settings, job, cancellationToken);
            UpdateDelivery(delivery.Id, result);
        }
    }

    private void RegisterDeliveries(NotificationSettings settings, IReadOnlyCollection<string> channels)
    {
        var snapshot = store.Read();
        var missing = snapshot.TransferJobs
            .Where(job => job.UpdateDate >= settings.ActivationDate &&
                (job.State == "Completed" && settings.NotifyOnSuccess || job.State == "Fehler" && settings.NotifyOnFailure))
            .SelectMany(job => channels.Select(channel => new
            {
                Job = job,
                Channel = channel,
                Event = job.State == "Completed" ? "JobSucceeded" : "JobFailed"
            }))
            .Where(candidate => !snapshot.NotificationDeliveries.Any(item =>
                item.TransferJobId == candidate.Job.Id && item.Event == candidate.Event && item.Channel == candidate.Channel))
            .ToList();
        if (missing.Count == 0) return;

        store.Update(data =>
        {
            foreach (var candidate in missing)
            {
                if (data.NotificationDeliveries.Any(item => item.TransferJobId == candidate.Job.Id && item.Event == candidate.Event && item.Channel == candidate.Channel)) continue;
                var now = DateTimeOffset.UtcNow;
                data.NotificationDeliveries.Add(new NotificationDelivery
                {
                    Id = store.NextId(data.NotificationDeliveries.Select(item => item.Id)),
                    TransferJobId = candidate.Job.Id,
                    Event = candidate.Event,
                    Channel = candidate.Channel,
                    State = "Pending",
                    NextAttemptDate = now,
                    CreateDate = now,
                    UpdateDate = now
                });
            }
        });
    }

    private void UpdateDelivery(long id, NotificationSendResult result)
    {
        store.Update(data =>
        {
            var item = data.NotificationDeliveries.FirstOrDefault(current => current.Id == id);
            if (item is null) return;
            item.Attempt++;
            item.State = result.Success ? "Sent" : "Failed";
            item.Error = result.Success ? "" : result.Message;
            item.SentDate = result.Success ? DateTimeOffset.UtcNow : null;
            item.NextAttemptDate = result.Success || item.Attempt >= 3 ? null : DateTimeOffset.UtcNow.AddMinutes(item.Attempt);
            item.UpdateDate = DateTimeOffset.UtcNow;
        });
        if (!result.Success) logger.LogWarning("Notification delivery {DeliveryId} failed: {Message}", id, result.Message);
    }

    private static IReadOnlyList<string> EnabledChannels(NotificationSettings settings)
    {
        var channels = new List<string>();
        if (settings.WebhookEnabled) channels.Add("Webhook");
        if (settings.EmailEnabled) channels.Add("Email");
        return channels;
    }
}
