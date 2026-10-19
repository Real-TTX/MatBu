using System.Text.Json;
using MatBu.Services;
using MatPlatform;

namespace MatBu.Platform;

/// <summary>
/// MatBu as an app of the platform contract (MatDesign docs/PLATFORM.md), on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how Mattiverse shows it (<see cref="Describe"/>: tiles, settings, buttons), what its values are and what the buttons do. The values come from
/// MatBu's services; the settings are the time zone of the schedule (Einstellungen → Allgemein) and the notifications (Benutzer → Benachrichtigungen),
/// checked by the same rules as those pages. Texts are German, or English when the caller asks for it with <c>Accept-Language</c>.
/// </summary>
public sealed class MatBuPlatform(
    MatBuStatusService status,
    GeneralSettingsStore generalSettings,
    NotificationSettingsStore notificationSettings,
    MailServer mailServer,
    ILogger<MatBuPlatform> logger) : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of MatBu in the platform (MatDesign AppRegistry.cs) and the default client id in MatIdentity.</summary>
    public const string AppId = "matbu";

    public const string Accent = "#0b7f8a";

    /// <summary>The cookie scheme the OpenID Connect handler of MatIdentity signs in with; MatBu itself keeps its own session cookie.</summary>
    public const string ExternalCookieScheme = "MatBu.External";

    /// <summary>The MatApiDefinition of MatBu (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are filled in by MatPlatform.</summary>
    public static void Describe(MatPlatformBuilder o) => Describe(o, new MatSmtpSettings());

    /// <summary>
    /// The definition with the mail through MatSMTP in <paramref name="smtp"/>: when it is configured, the SMTP server fields are fixed
    /// (<see cref="MatSmtpSettings.Fix"/>: shown, not changeable, <c>PUT /api/configure</c> refuses them); whether e-mail is on and the recipients stay editable.
    /// </summary>
    public static void Describe(MatPlatformBuilder o, MatSmtpSettings smtp)
    {
        var T = o.T;
        void Field(MatField field) => o.Field(MailServer.FixedKeys.Contains(field.Key) ? smtp.Fix(field, o.Language) : field);

        o.App(
            AppId,
            "MatBu",
            T("Backup und Sync für Dateien, Docker-Volumes und Proxmox", "Backup and sync for files, Docker volumes and Proxmox"),
            MatBuVersion.Current,
            "/favicon.svg",
            Accent);

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("activeTasks", "lastResult", "lastBackup", "nextRun", "runningJobs", "failedJobs24h", "storageUsed", "objectsAttention"),
            MatSection.Actions("open", "openJobs", "runBackups"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Zeitplan", "Schedule"), "timeZone"),
            MatSection.Form(T("Webhook", "Webhook"), "webhookEnabled", "webhookUrl"),
            MatSection.Form(T("E-Mail", "E-mail"), "emailEnabled", "smtpHost", "smtpPort", "smtpUsername", "smtpPassword", "mailFrom", "mailRecipients"),
            MatSection.Actions("openSettings", "openNotifications"));

        o.Tile("activeTasks", T("Aktive Backup-Jobs", "Active backup jobs"), icon: "list");
        o.Tile(new MatStateItem("lastResult", T("Letztes Backup", "Last backup"), "status") { Icon = "check-circle" });
        o.Tile("lastBackup", T("Letztes erfolgreiches Backup", "Last successful backup"), "time", icon: "archive");
        o.Tile("nextRun", T("Nächster geplanter Lauf", "Next scheduled run"), "time", icon: "calendar");
        o.Tile("runningJobs", T("Laufende Ausführungen", "Running jobs"), icon: "progress-play");
        o.Tile(new MatStateItem("failedJobs24h", T("Fehlgeschlagen (24 h)", "Failed (24 h)"), "number") { Icon = "alert", ErrorAbove = 0 });
        o.Tile("storageUsed", T("Belegter Backup-Speicher", "Backup storage used"), icon: "hard-drive", unit: "bytes");
        o.Tile(new MatStateItem("objectsAttention", T("Objekte mit Warnung", "Objects needing attention"), "number") { Icon = "server", WarnAbove = 0 });

        o.Field(new MatField("timeZone", T("Zeitzone des Zeitplans", "Time zone of the schedule"), "text")
        {
            Required = true,
            Max = 100,
            Placeholder = "Europe/Berlin",
            Hint = T("IANA-Name wie Europe/Berlin; tägliche und wöchentliche Zeitpläne gelten in dieser Zeitzone.",
                "An IANA name like Europe/Berlin; daily and weekly schedules run in this time zone."),
        });
        o.Field(new MatField("webhookEnabled", T("Webhook bei Backup-Ergebnissen", "Webhook on backup results"), "bool")
        {
            Hint = T("Meldet fehlgeschlagene (und auf der Benachrichtigungsseite wählbar: erfolgreiche) Jobs als JSON.",
                "Reports failed (and, chosen on the notifications page, successful) jobs as JSON."),
        });
        o.Field(new MatField("webhookUrl", T("Webhook-Adresse", "Webhook address"), "url") { Required = true, Max = 2000, ShowIf = MatField.When(("webhookEnabled", true)) });
        o.Field(new MatField("emailEnabled", T("E-Mail bei Backup-Ergebnissen", "E-mail on backup results"), "bool"));
        Field(new MatField("smtpHost", T("SMTP-Server", "SMTP server"), "text") { Required = true, Max = 255, Placeholder = "mail.example.com", ShowIf = MatField.When(("emailEnabled", true)) });
        Field(new MatField("smtpPort", T("SMTP-Port", "SMTP port"), "number") { Required = true, Min = 1, Max = 65535, ShowIf = MatField.When(("emailEnabled", true)) });
        Field(new MatField("smtpUsername", T("SMTP-Benutzer", "SMTP user"), "text") { Max = 255, ShowIf = MatField.When(("emailEnabled", true)) });
        Field(new MatField("smtpPassword", T("SMTP-Passwort", "SMTP password"), "secret") { ShowIf = MatField.When(("emailEnabled", true)) });
        Field(new MatField("mailFrom", T("Absender", "Sender"), "email") { Required = true, Max = 255, ShowIf = MatField.When(("emailEnabled", true)) });
        o.Field(new MatField("mailRecipients", T("Empfänger", "Recipients"), "text")
        {
            Required = true,
            Max = 1000,
            Hint = T("Mehrere Adressen mit Komma getrennt.", "Several addresses separated by commas."),
            ShowIf = MatField.When(("emailEnabled", true)),
        });

        o.LinkAction("open", T("MatBu öffnen", "Open MatBu"), "external", "/");
        o.LinkAction("openJobs", T("Ausführungen", "Job runs"), "list", "/Jobs");
        o.PostAction("runBackups", T("Alle Backups jetzt starten", "Run all backups now"), "play",
            T("Alle aktiven Backup-Jobs jetzt ausführen?", "Run all active backup jobs now?"));
        o.LinkAction("openSettings", T("Alle Einstellungen", "All settings"), "settings", "/Settings/General");
        o.LinkAction("openNotifications", T("Benachrichtigungen", "Notifications"), "bell", "/Settings/Notifications");

        o.Role("admin", T("Administrator", "Administrator"));
        o.Role("operator", T("Operator", "Operator"));
        o.Role("user", T("Benutzer (nur lesen)", "User (read only)"));
    }

    public Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        if (!status.CanReadDatabase())
        {
            return Task.FromResult<IReadOnlyList<MatHealthCheck>>(
                [new MatHealthCheck("database", "down", call.T("Die Datenbank kann nicht gelesen werden.", "The database cannot be read."))]);
        }

        var current = status.GetStatus();
        var checks = new List<MatHealthCheck> { new("database", "ok") };

        checks.Add(current.FailingTasks > 0 || current.FailedJobs24Hours > 0
            ? new MatHealthCheck("backups", "degraded", call.T(
                $"{current.FailingTasks} Job(s) im Fehlerzustand, {current.FailedJobs24Hours} fehlgeschlagene Ausführung(en) in 24 Stunden.",
                $"{current.FailingTasks} job(s) failing, {current.FailedJobs24Hours} failed run(s) in 24 hours."))
            : new MatHealthCheck("backups", "ok"));

        checks.Add(current.ObjectsNeedingAttention > 0
            ? new MatHealthCheck("objects", "degraded", call.T(
                $"{current.ObjectsNeedingAttention} Objekt(e) nicht erreichbar.",
                $"{current.ObjectsNeedingAttention} object(s) not reachable."))
            : new MatHealthCheck("objects", "ok"));

        if (current.SecondariesOffline > 0)
        {
            checks.Add(new MatHealthCheck("secondaries", "degraded", call.T(
                $"{current.SecondariesOffline} Secondary-Instanz(en) offline.",
                $"{current.SecondariesOffline} secondary instance(s) offline.")));
        }

        return Task.FromResult<IReadOnlyList<MatHealthCheck>>(checks);
    }

    public Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var current = status.GetStatus();
        var lastResult = current.LastResult switch
        {
            BackupRunResult.Succeeded => new MatStateValue("ok", "ok", call.T($"Erfolgreich: {current.LastResultTask}", $"Succeeded: {current.LastResultTask}")),
            BackupRunResult.Failed => new MatStateValue("error", "error", call.T($"Fehlgeschlagen: {current.LastResultTask}", $"Failed: {current.LastResultTask}")),
            BackupRunResult.Cancelled => new MatStateValue("warn", "warn", call.T($"Abgebrochen: {current.LastResultTask}", $"Cancelled: {current.LastResultTask}")),
            _ => new MatStateValue("info", "info", call.T("Noch kein Backup gelaufen", "No backup has run yet")),
        };

        IReadOnlyDictionary<string, MatStateValue> values = new Dictionary<string, MatStateValue>
        {
            ["activeTasks"] = new(current.ActiveTasks),
            ["lastResult"] = lastResult,
            ["lastBackup"] = new(current.LastSuccessfulBackup),
            ["nextRun"] = new(current.NextRun),
            ["runningJobs"] = new(current.RunningJobs),
            ["failedJobs24h"] = new(current.FailedJobs24Hours, current.FailedJobs24Hours > 0 ? "error" : "ok"),
            ["storageUsed"] = new(current.StorageBytes),
            ["objectsAttention"] = new(current.ObjectsNeedingAttention, current.ObjectsNeedingAttention > 0 ? "warn" : "ok"),
        };
        return Task.FromResult(values);
    }

    public Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var general = generalSettings.Read();
        // The values in force: MatSMTP's server, user, password and sender when Mattiverse provides it.
        var notifications = mailServer.InForce(notificationSettings.Read());
        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["timeZone"] = general.TimeZoneId,
            ["webhookEnabled"] = notifications.WebhookEnabled,
            ["webhookUrl"] = notifications.WebhookUrl,
            ["emailEnabled"] = notifications.EmailEnabled,
            ["smtpHost"] = notifications.SmtpHost,
            ["smtpPort"] = notifications.SmtpPort,
            ["smtpUsername"] = notifications.SmtpUsername,
            ["smtpPassword"] = MatSecret.Of(notifications.SmtpPassword),
            ["mailFrom"] = notifications.MailFrom,
            ["mailRecipients"] = notifications.MailRecipients,
        };
        return Task.FromResult(values);
    }

    /// <summary>
    /// Saves the changed settings. MatPlatform has already checked them against the definition (types, required, limits, addresses);
    /// here the rules of MatBu's settings pages follow, and nothing is saved when one of them fails.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var general = generalSettings.Read();
        var notifications = notificationSettings.Read();
        var generalChanged = false;
        var notificationsChanged = false;
        var clearPassword = false;

        foreach (var (key, value) in values)
        {
            // MatPlatform refuses a fixed field already (MatValueChecker); this keeps it so when the check of the values is off.
            if (mailServer.IsFixed && MailServer.FixedKeys.Contains(key))
            {
                errors[key] = mailServer.Describe(call.Language)!;
                continue;
            }

            switch (key)
            {
                case "timeZone":
                    var zone = Text(value);
                    if (!GeneralSettingsStore.IsKnownTimeZone(zone))
                    {
                        errors[key] = call.T("Diese Zeitzone ist auf diesem System nicht bekannt.", "This time zone is not known on this system.");
                    }

                    general.TimeZoneId = zone.Trim();
                    generalChanged = true;
                    break;
                case "webhookEnabled":
                    notifications.WebhookEnabled = value.ValueKind == JsonValueKind.True;
                    notificationsChanged = true;
                    break;
                case "webhookUrl":
                    notifications.WebhookUrl = Text(value);
                    notificationsChanged = true;
                    break;
                case "emailEnabled":
                    notifications.EmailEnabled = value.ValueKind == JsonValueKind.True;
                    notificationsChanged = true;
                    break;
                case "smtpHost":
                    notifications.SmtpHost = Text(value);
                    notificationsChanged = true;
                    break;
                case "smtpPort":
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port))
                    {
                        notifications.SmtpPort = port;
                    }
                    else
                    {
                        errors[key] = call.T("Bitte eine ganze Zahl angeben.", "Send a whole number.");
                    }

                    notificationsChanged = true;
                    break;
                case "smtpUsername":
                    notifications.SmtpUsername = Text(value);
                    notificationsChanged = true;
                    break;
                case "smtpPassword":
                    // A secret that is sent is set; "" (or null) clears it.
                    notifications.SmtpPassword = Text(value);
                    clearPassword = notifications.SmtpPassword.Length == 0;
                    notificationsChanged = true;
                    break;
                case "mailFrom":
                    notifications.MailFrom = Text(value);
                    notificationsChanged = true;
                    break;
                case "mailRecipients":
                    notifications.MailRecipients = Text(value);
                    notificationsChanged = true;
                    break;
                default:
                    errors[key] = call.T("Diese Einstellung gibt es nicht.", "There is no such setting.");
                    break;
            }
        }

        if (notificationsChanged && mailServer.Validate(notifications) is { } problem)
        {
            errors.TryAdd(string.Empty, problem);
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(errors);
        }

        if (generalChanged)
        {
            generalSettings.Save(general);
        }

        if (notificationsChanged)
        {
            notificationSettings.Save(notifications, clearPassword);
        }

        logger.LogInformation(
            "Settings changed through the platform API by {Caller}: {Keys}",
            call.Caller,
            string.Join(", ", values.Keys));
        return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    public Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        switch (id)
        {
            case "runBackups":
                var result = status.QueueAllBackups();
                logger.LogInformation("All backups queued through the platform API by {Caller}: {Queued} queued, {Already} already queued", call.Caller, result.Queued, result.AlreadyQueued);
                if (result.Queued == 0 && result.AlreadyQueued == 0)
                {
                    return Task.FromResult<MatActionResult?>(new MatActionResult(false, call.T("Es gibt keinen aktiven Backup-Job.", "There is no active backup job.")));
                }

                return Task.FromResult<MatActionResult?>(new MatActionResult(true, call.T(
                    $"{result.Queued} Backup-Job(s) gestartet, {result.AlreadyQueued} lief(en) bereits.",
                    $"{result.Queued} backup job(s) started, {result.AlreadyQueued} already running.")));
            default:
                return Task.FromResult<MatActionResult?>(null);
        }
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
}
