using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Settings;

public sealed class NotificationsModel(
    PersistentStore store,
    NotificationSettingsStore settingsStore,
    NotificationService notificationService,
    MailServer mailServer) : AppPageModel(store)
{
    [BindProperty] public NotificationSettings Input { get; set; } = new();
    public bool HasStoredPassword { get; private set; }
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }
    public IReadOnlyList<NotificationDeliveryRow> Deliveries { get; private set; } = [];

    /// <summary>True when the mail goes through MatSMTP: the server fields are shown disabled and nothing is saved for them.</summary>
    public bool SmtpFixed => mailServer.IsFixed;

    /// <summary>The sentence that says where the mail goes when <see cref="SmtpFixed"/>.</summary>
    public string? SmtpFixedText => mailServer.Describe("de");

    public IActionResult OnGet()
    {
        if (!LoadAdmin()) return CurrentUser is null ? RedirectToPage("/Login") : Forbid();
        LoadSettings();
        return Page();
    }

    public IActionResult OnPostSave()
    {
        if (!LoadAdmin()) return CurrentUser is null ? RedirectToPage("/Login") : Forbid();
        Input = mailServer.KeepFixed(Input, settingsStore.Read());
        var error = mailServer.Validate(Input);
        if (error is not null) return Show(error, true);
        settingsStore.Save(Input);
        LoadSettings();
        return Show("Benachrichtigungseinstellungen gespeichert. Neue Ereignisse werden ab jetzt berücksichtigt.", false);
    }

    public async Task<IActionResult> OnPostTestWebhookAsync(CancellationToken cancellationToken)
    {
        if (!LoadAdmin()) return CurrentUser is null ? RedirectToPage("/Login") : Forbid();
        if (string.IsNullOrWhiteSpace(Input.WebhookUrl)) return Show("Für den Webhook-Test ist eine URL erforderlich.", true);
        var settings = settingsStore.Save(mailServer.KeepFixed(Input, settingsStore.Read()));
        var result = await notificationService.SendWebhookTestAsync(settings, cancellationToken);
        LoadSettings();
        return Show(result.Message, !result.Success);
    }

    public async Task<IActionResult> OnPostTestEmailAsync(CancellationToken cancellationToken)
    {
        if (!LoadAdmin()) return CurrentUser is null ? RedirectToPage("/Login") : Forbid();
        var settings = settingsStore.Save(mailServer.KeepFixed(Input, settingsStore.Read()));
        var result = await notificationService.SendEmailTestAsync(settings, cancellationToken);
        LoadSettings();
        return Show(result.Message, !result.Success);
    }

    private bool LoadAdmin()
    {
        if (!LoadUser()) return false;
        ViewData["UserName"] = CurrentUser!.UserName;
        return CurrentUser.Role == UserRole.Admin;
    }

    private void LoadSettings()
    {
        // With MatSMTP the page shows its values (never its password); MatBu's own stay stored for when the variables are gone.
        Input = mailServer.InForce(settingsStore.Read());
        HasStoredPassword = !string.IsNullOrEmpty(Input.SmtpPassword);
        Input.SmtpPassword = "";
        LoadDeliveries();
    }

    private IActionResult Show(string message, bool error)
    {
        Message = message;
        MessageIsError = error;
        HasStoredPassword = HasStoredPassword || !string.IsNullOrEmpty(mailServer.InForce(settingsStore.Read()).SmtpPassword);
        if (SmtpFixed)
        {
            Input = mailServer.InForce(Input);
            Input.SmtpPassword = "";
            foreach (var field in new[] { nameof(Input.SmtpHost), nameof(Input.SmtpPort), nameof(Input.SmtpUseSsl), nameof(Input.SmtpUsername), nameof(Input.SmtpPassword), nameof(Input.MailFrom) })
                ModelState.Remove($"{nameof(Input)}.{field}");
        }
        LoadDeliveries();
        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }

    private void LoadDeliveries()
    {
        var data = Store.Read();
        var jobs = data.TransferJobs.ToDictionary(item => item.Id);
        Deliveries = data.NotificationDeliveries.OrderByDescending(item => item.UpdateDate).Take(20)
            .Select(item => new NotificationDeliveryRow(
                item.Id,
                item.TransferJobId,
                jobs.GetValueOrDefault(item.TransferJobId)?.TaskName ?? $"Job #{item.TransferJobId}",
                item.Event,
                item.Channel,
                item.State,
                item.Attempt,
                item.Error,
                item.UpdateDate))
            .ToList();
    }

    public sealed record NotificationDeliveryRow(long Id, long JobId, string JobName, string Event, string Channel, string State, int Attempt, string Error, DateTimeOffset UpdateDate);
}
