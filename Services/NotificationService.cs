using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using System.Text.Json;
using MatBu.Models;
using Microsoft.AspNetCore.DataProtection;

namespace MatBu.Services;

public class NotificationSettings
{
    public bool WebhookEnabled { get; set; }
    public string WebhookUrl { get; set; } = "";
    public bool EmailEnabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string MailFrom { get; set; } = "";
    public string MailRecipients { get; set; } = "";
    public bool NotifyOnSuccess { get; set; }
    public bool NotifyOnFailure { get; set; } = true;
    public DateTimeOffset ActivationDate { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class StoredNotificationSettings : NotificationSettings
{
    public string ProtectedSmtpPassword { get; set; } = "";
}

public sealed class NotificationSettingsStore
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public NotificationSettingsStore(IHostEnvironment environment, IDataProtectionProvider protectionProvider)
    {
        var directory = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "notifications.json");
        _protector = protectionProvider.CreateProtector("MatBu.NotificationSettings.v1");
    }

    public NotificationSettings Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new NotificationSettings();
            var stored = JsonSerializer.Deserialize<StoredNotificationSettings>(File.ReadAllText(_path), JsonOptions) ?? new StoredNotificationSettings();
            return Copy(stored, Unprotect(stored.ProtectedSmtpPassword));
        }
    }

    public NotificationSettings Save(NotificationSettings input)
    {
        lock (_gate)
        {
            var existing = File.Exists(_path)
                ? JsonSerializer.Deserialize<StoredNotificationSettings>(File.ReadAllText(_path), JsonOptions) ?? new StoredNotificationSettings()
                : new StoredNotificationSettings();
            var password = string.IsNullOrEmpty(input.SmtpPassword) ? Unprotect(existing.ProtectedSmtpPassword) : input.SmtpPassword;
            var stored = new StoredNotificationSettings
            {
                WebhookEnabled = input.WebhookEnabled,
                WebhookUrl = Normalize(input.WebhookUrl),
                EmailEnabled = input.EmailEnabled,
                SmtpHost = Normalize(input.SmtpHost),
                SmtpPort = input.SmtpPort,
                SmtpUseSsl = input.SmtpUseSsl,
                SmtpUsername = Normalize(input.SmtpUsername),
                ProtectedSmtpPassword = string.IsNullOrEmpty(password) ? "" : _protector.Protect(password),
                MailFrom = Normalize(input.MailFrom),
                MailRecipients = Normalize(input.MailRecipients),
                NotifyOnSuccess = input.NotifyOnSuccess,
                NotifyOnFailure = input.NotifyOnFailure,
                ActivationDate = DateTimeOffset.UtcNow
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(stored, JsonOptions));
            return Copy(stored, password);
        }
    }

    private string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return _protector.Unprotect(value); }
        catch { return ""; }
    }

    private static string Normalize(string? value) => value?.Trim() ?? "";

    private static NotificationSettings Copy(NotificationSettings source, string password) => new()
    {
        WebhookEnabled = source.WebhookEnabled,
        WebhookUrl = source.WebhookUrl,
        EmailEnabled = source.EmailEnabled,
        SmtpHost = source.SmtpHost,
        SmtpPort = source.SmtpPort,
        SmtpUseSsl = source.SmtpUseSsl,
        SmtpUsername = source.SmtpUsername,
        SmtpPassword = password,
        MailFrom = source.MailFrom,
        MailRecipients = source.MailRecipients,
        NotifyOnSuccess = source.NotifyOnSuccess,
        NotifyOnFailure = source.NotifyOnFailure,
        ActivationDate = source.ActivationDate
    };
}

public sealed record NotificationSendResult(bool Success, string Message);

public sealed class NotificationService(IHttpClientFactory httpClientFactory)
{
    public async Task<NotificationSendResult> SendWebhookTestAsync(NotificationSettings settings, CancellationToken cancellationToken) =>
        await SendWebhookAsync(settings, new
        {
            source = "MatBu",
            eventType = "Test",
            status = "Healthy",
            message = "MatBu Webhook-Test erfolgreich ausgelöst.",
            timestamp = DateTimeOffset.UtcNow
        }, cancellationToken);

    public Task<NotificationSendResult> SendEmailTestAsync(NotificationSettings settings, CancellationToken cancellationToken) =>
        SendEmailAsync(settings, "MatBu · Testbenachrichtigung", "Die E-Mail-Benachrichtigung von MatBu funktioniert.", cancellationToken);

    public Task<NotificationSendResult> SendJobWebhookAsync(NotificationSettings settings, TransferJob job, CancellationToken cancellationToken) =>
        SendWebhookAsync(settings, BuildPayload(job), cancellationToken);

    public Task<NotificationSendResult> SendJobEmailAsync(NotificationSettings settings, TransferJob job, CancellationToken cancellationToken)
    {
        var successful = job.State == "Completed";
        var subject = $"MatBu · {(successful ? "Erfolgreich" : "Fehlgeschlagen")} · {job.TaskName}";
        var body = $"Job #{job.Id}: {job.TaskName}\nStatus: {job.State}\nRoute: {job.SourceInstanceName} / {job.SourceObjectName} → {job.TargetInstanceName} / {job.TargetObjectName}\nÜbertragen: {job.BytesTransferred:N0} Bytes\nZiel: {job.ResolvedDestination}\nFehler: {job.Error}\nZeitpunkt: {job.UpdateDate:O}";
        return SendEmailAsync(settings, subject, body, cancellationToken);
    }

    private async Task<NotificationSendResult> SendWebhookAsync(NotificationSettings settings, object payload, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(settings.WebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new(false, "Webhook-URL muss eine absolute HTTP- oder HTTPS-Adresse sein.");
        try
        {
            using var response = await httpClientFactory.CreateClient("Notifications").PostAsJsonAsync(uri, payload, cancellationToken);
            if (response.IsSuccessStatusCode) return new(true, $"Webhook antwortete mit HTTP {(int)response.StatusCode}.");
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            return new(false, $"Webhook antwortete mit HTTP {(int)response.StatusCode}: {Limit(detail)}");
        }
        catch (Exception exception) { return new(false, $"Webhook fehlgeschlagen: {exception.Message}"); }
    }

    private static async Task<NotificationSendResult> SendEmailAsync(NotificationSettings settings, string subject, string body, CancellationToken cancellationToken)
    {
        var recipients = (settings.MailRecipients ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrWhiteSpace(settings.SmtpHost) || settings.SmtpPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(settings.MailFrom) || recipients.Length == 0)
            return new(false, "SMTP-Host, Port, Absender und mindestens ein Empfänger sind erforderlich.");
        try
        {
            using var message = new MailMessage { From = new MailAddress(settings.MailFrom), Subject = subject, Body = body };
            foreach (var recipient in recipients) message.To.Add(new MailAddress(recipient));
            using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
            {
                EnableSsl = settings.SmtpUseSsl,
                Credentials = string.IsNullOrWhiteSpace(settings.SmtpUsername)
                    ? CredentialCache.DefaultNetworkCredentials
                    : new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword)
            };
            await client.SendMailAsync(message, cancellationToken);
            return new(true, $"Test-E-Mail an {recipients.Length} Empfänger gesendet.");
        }
        catch (Exception exception) { return new(false, $"E-Mail fehlgeschlagen: {exception.Message}"); }
    }

    private static object BuildPayload(TransferJob job) => new
    {
        source = "MatBu",
        eventType = job.State == "Completed" ? "JobSucceeded" : "JobFailed",
        job = new { job.Id, job.TaskId, job.TaskName, job.State, job.Attempt, job.BytesTransferred, job.TotalBytes, job.SourceBytes, job.StoredBytes, job.Error },
        route = new { sourceInstance = job.SourceInstanceName, sourceObject = job.SourceObjectName, targetInstance = job.TargetInstanceName, targetObject = job.TargetObjectName, destination = job.ResolvedDestination },
        timestamp = job.UpdateDate
    };

    private static string Limit(string value) => value.Length <= 300 ? value : value[..300];
}
