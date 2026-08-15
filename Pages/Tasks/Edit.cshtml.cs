using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MatBu.Pages.Tasks;

public class EditModel(PersistentStore store, SourceBrowserService sourceBrowser, SecondaryCommandService commands) : AppPageModel(store)
{
    private static readonly int[] ValidChunkSizesMiB = [4, 8, 16, 32];

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public BackupTask Input { get; set; } = new()
    {
        Method = BackupMethod.ReverseIncremental,
        ChunkSizeMiB = 8,
        Compression = BackupCompression.None
    };
    [BindProperty] public List<long> SelectedLabelIds { get; set; } = [];
    [BindProperty] public List<string> SelectedSourcePaths { get; set; } = [];
    [BindProperty] public string ScheduleKind { get; set; } = "Daily";
    [BindProperty] public int ScheduleInterval { get; set; } = 2;
    [BindProperty] public string ScheduleIntervalUnit { get; set; } = "Hours";
    [BindProperty] public string ScheduleTime { get; set; } = "02:00";
    [BindProperty] public string ScheduleWeekday { get; set; } = nameof(DayOfWeek.Sunday);

    public IReadOnlyList<BackupObject> Objects { get; private set; } = [];
    public IReadOnlyList<JobLabel> JobLabels { get; private set; } = [];
    public IReadOnlyList<int> AllowedChunkSizesMiB => ValidChunkSizesMiB;
    public bool CanManageConsistency => CurrentUser?.Role == UserRole.Admin;
    public string? Error { get; private set; }

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role == UserRole.User) return Forbid();

        var data = Store.Read();
        Objects = data.Objects;
        JobLabels = data.JobLabels.OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (Id is not null)
        {
            Input = data.Tasks.FirstOrDefault(x => x.Id == Id) ?? new BackupTask
            {
                Method = BackupMethod.ReverseIncremental,
                ChunkSizeMiB = 8,
                Compression = BackupCompression.None
            };
            SelectedLabelIds = data.BackupTaskLabels.Where(item => item.BackupTaskId == Id).Select(item => item.JobLabelId).ToList();
            SelectedSourcePaths = SourceSelection.Parse(Input.SourceSelectionJson).ToList();
        }

        if (Input.Method == BackupMethod.ReverseIncremental && !ValidChunkSizesMiB.Contains(Input.ChunkSizeMiB))
            Input.ChunkSizeMiB = 8;
        Input.Schedule = BackupSchedule.Normalize(Input.Schedule);
        LoadScheduleFields(Input.Schedule);

        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role == UserRole.User) return Forbid();

        var data = Store.Read();
        Objects = data.Objects;
        JobLabels = data.JobLabels.OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase).ToList();
        SelectedLabelIds = SelectedLabelIds.Distinct().ToList();
        if (SelectedLabelIds.Any(labelId => !data.JobLabels.Any(label => label.Id == labelId)))
            ModelState.AddModelError(nameof(SelectedLabelIds), "Mindestens ein ausgewählter Tag existiert nicht.");
        if (!BackupSchedule.TryBuild(ScheduleKind, ScheduleInterval, ScheduleIntervalUnit, ScheduleTime, ScheduleWeekday, out var schedule, out var scheduleError))
            ModelState.AddModelError(nameof(ScheduleKind), scheduleError ?? "Der Zeitplan ist ungültig.");
        Input.Schedule = schedule;
        Input.SourceSelectionJson = SourceSelection.Serialize(SelectedSourcePaths);
        ValidateBackupMethod();
        ValidateRetryPolicy();
        ValidateConsistency(data);

        var source = Objects.FirstOrDefault(x => x.Id == Input.SourceId);
        var target = Objects.FirstOrDefault(x => x.Id == Input.TargetId);
        if (source is null || target is null || source.Direction == ObjectDirection.Target || target.Direction == ObjectDirection.Source || source.Id == target.Id)
            ModelState.AddModelError(string.Empty, "Quelle und Ziel müssen kompatible, unterschiedliche Objekte sein.");

        if (!ModelState.IsValid) return Page();

        Store.Update(current =>
        {
            var now = DateTimeOffset.UtcNow;
            if (Id is null)
            {
                Input.Id = Store.NextId(current.Tasks.Select(x => x.Id));
                Input.CreateDate = Input.UpdateDate = now;
                Input.CreateUserId = Input.UpdateUserId = CurrentUser!.Id;
                Input.NextRunDate = Input.Enabled ? BackupSchedule.GetNextOccurrenceUtc(Input.Schedule, now) : null;
                current.Tasks.Add(Input);
                AddLabelAssignments(current, Input.Id, SelectedLabelIds, Input.CreateDate);
                return;
            }

            var item = current.Tasks.First(x => x.Id == Id);
            item.Name = Input.Name;
            item.SourceId = Input.SourceId;
            item.TargetId = Input.TargetId;
            item.Schedule = Input.Schedule;
            item.Retention = Input.Retention;
            item.Enabled = Input.Enabled;
            item.Method = Input.Method;
            item.Compression = Input.Compression;
            item.SourceSelectionJson = Input.SourceSelectionJson;
            item.ChunkSizeMiB = Input.ChunkSizeMiB;
            item.MaxRetryAttempts = Input.MaxRetryAttempts;
            item.RetryDelayMinutes = Input.RetryDelayMinutes;
            item.ConsistencyMode = Input.ConsistencyMode;
            item.ConsistencyContainerNames = Input.ConsistencyContainerNames;
            item.PreBackupCommand = Input.PreBackupCommand;
            item.PostBackupCommand = Input.PostBackupCommand;
            item.ConsistencyTimeoutSeconds = Input.ConsistencyTimeoutSeconds;
            item.NextRunDate = Input.Enabled ? BackupSchedule.GetNextOccurrenceUtc(Input.Schedule, now) : null;
            item.UpdateDate = now;
            item.UpdateUserId = CurrentUser!.Id;
            current.BackupTaskLabels.RemoveAll(assignment => assignment.BackupTaskId == item.Id);
            AddLabelAssignments(current, item.Id, SelectedLabelIds, item.UpdateDate);
        });

        return RedirectToPage("/Tasks/Index");
    }

    public async Task<IActionResult> OnGetBrowseSourceAsync(long objectId, string? path, CancellationToken cancellationToken)
    {
        if (!LoadUser()) return Unauthorized();
        var data = Store.Read();
        var source = data.Objects.FirstOrDefault(item => item.Id == objectId);
        if (source is null) return NotFound(new { message = "Das Quell-Object wurde nicht gefunden." });
        var instance = data.Instances.FirstOrDefault(item => item.Id == source.InstanceId);
        if (instance is null) return NotFound(new { message = "Die zugeordnete Instanz wurde nicht gefunden." });
        if (source.Kind is not (ObjectKind.LocalFolder or ObjectKind.Smb or ObjectKind.DockerVolume))
            return BadRequest(new { message = "Für diesen Quelltyp ist keine Ordnerauswahl verfügbar." });

        try
        {
            var credential = Store.GetSmbCredential(source.Id);
            if (instance.Role == InstanceRole.Primary)
                return new JsonResult(await sourceBrowser.BrowseAsync(source, path, credential, cancellationToken));

            var transferId = Guid.NewGuid().ToString("N");
            var request = new SourceBrowseRequest(source.Kind, source.Location, path ?? "", credential?.Username, credential?.Password);
            var commandId = commands.Queue(instance.Id, SecondaryCommandKind.BrowseSource, transferId, request);
            var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
            if (command.State != "Completed") return BadRequest(new { message = string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte den Ordner nicht laden." : command.Error });
            var result = JsonSerializer.Deserialize<SourceBrowseResult>(command.ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return result is null ? BadRequest(new { message = "Die Secondary lieferte keine Ordnerliste." }) : new JsonResult(result);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private void LoadScheduleFields(string schedule)
    {
        if (!BackupSchedule.TryParse(schedule, out var definition)) return;
        ScheduleTime = definition.Time.ToString("HH:mm");
        ScheduleWeekday = definition.DayOfWeek.ToString();
        ScheduleInterval = definition.IntervalValue > 0 ? definition.IntervalValue : 2;
        ScheduleIntervalUnit = definition.Kind == BackupScheduleKind.IntervalMinutes ? "Minutes" : "Hours";
        ScheduleKind = definition.Kind switch
        {
            BackupScheduleKind.IntervalMinutes or BackupScheduleKind.IntervalHours => "Interval",
            BackupScheduleKind.Weekly => "Weekly",
            _ => "Daily"
        };
    }

    private void ValidateBackupMethod()
    {
        if (Input.Method is not (BackupMethod.Full or BackupMethod.ReverseIncremental))
        {
            ModelState.AddModelError("Input.Method", "Bitte eine gültige Backup-Methode auswählen.");
            return;
        }

        if (Input.Method == BackupMethod.Full)
        {
            Input.ChunkSizeMiB = 0;
            if (Input.Compression is not (BackupCompression.None or BackupCompression.Fast or BackupCompression.Balanced or BackupCompression.Maximum))
                ModelState.AddModelError("Input.Compression", "Bitte ein gültiges Kompressionsprofil auswählen.");
            return;
        }

        Input.Compression = BackupCompression.None;
        if (!ValidChunkSizesMiB.Contains(Input.ChunkSizeMiB))
            ModelState.AddModelError("Input.ChunkSizeMiB", "Für Reverse Incremental sind 4, 8, 16 oder 32 MiB erlaubt.");
    }

    private void ValidateRetryPolicy()
    {
        if (Input.MaxRetryAttempts is < 1 or > 20)
            ModelState.AddModelError("Input.MaxRetryAttempts", "Es sind 1 bis 20 Versuche erlaubt. Mit 1 ist die automatische Wiederholung deaktiviert.");
        if (Input.RetryDelayMinutes is < 1 or > 1440)
            ModelState.AddModelError("Input.RetryDelayMinutes", "Die Basis-Wartezeit muss zwischen 1 und 1440 Minuten liegen.");
    }

    private void ValidateConsistency(AppData data)
    {
        Input.ConsistencyContainerNames = Input.ConsistencyContainerNames?.Trim() ?? "";
        Input.PreBackupCommand = Input.PreBackupCommand?.Trim() ?? "";
        Input.PostBackupCommand = Input.PostBackupCommand?.Trim() ?? "";
        if (!Enum.IsDefined(Input.ConsistencyMode))
            ModelState.AddModelError("Input.ConsistencyMode", "Bitte einen gültigen Konsistenzmodus auswählen.");

        var existing = Id is null ? null : data.Tasks.FirstOrDefault(item => item.Id == Id);
        if (CurrentUser!.Role != UserRole.Admin && !SameConsistency(existing, Input))
            ModelState.AddModelError("Input.ConsistencyMode", "Nur Administratoren dürfen die Docker-Konsistenzsteuerung ändern.");

        if (Input.ConsistencyMode == BackupConsistencyMode.None) return;
        if (Input.Method != BackupMethod.Full)
            ModelState.AddModelError("Input.ConsistencyMode", "Docker-Konsistenzsteuerung ist derzeit nur für Full-Backups verfügbar.");
        if (Input.ConsistencyTimeoutSeconds is < 5 or > 900)
            ModelState.AddModelError("Input.ConsistencyTimeoutSeconds", "Der Hook-Timeout muss zwischen 5 und 900 Sekunden liegen.");

        var containers = Input.ConsistencyContainerNames.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (Input.ConsistencyMode == BackupConsistencyMode.DockerPause && containers.Length == 0)
            ModelState.AddModelError("Input.ConsistencyContainerNames", "Gib mindestens einen zu pausierenden Container an.");
        if (Input.ConsistencyMode == BackupConsistencyMode.DockerExec)
        {
            if (containers.Length != 1) ModelState.AddModelError("Input.ConsistencyContainerNames", "Docker Exec benötigt genau einen Container.");
            if (string.IsNullOrWhiteSpace(Input.PreBackupCommand) && string.IsNullOrWhiteSpace(Input.PostBackupCommand))
                ModelState.AddModelError("Input.PreBackupCommand", "Gib mindestens ein Pre- oder Post-Kommando an.");
        }
    }

    private static bool SameConsistency(BackupTask? existing, BackupTask input) => existing is null
        ? input.ConsistencyMode == BackupConsistencyMode.None
        : existing.ConsistencyMode == input.ConsistencyMode &&
          existing.ConsistencyContainerNames == input.ConsistencyContainerNames &&
          existing.PreBackupCommand == input.PreBackupCommand &&
          existing.PostBackupCommand == input.PostBackupCommand &&
          existing.ConsistencyTimeoutSeconds == input.ConsistencyTimeoutSeconds;

    private void AddLabelAssignments(AppData data, long taskId, IEnumerable<long> labelIds, DateTimeOffset now)
    {
        var nextId = Store.NextId(data.BackupTaskLabels.Select(item => item.Id));
        foreach (var labelId in labelIds.Distinct())
            data.BackupTaskLabels.Add(new BackupTaskLabel
            {
                Id = nextId++,
                BackupTaskId = taskId,
                JobLabelId = labelId,
                CreateDate = now,
                CreateUserId = CurrentUser!.Id,
                UpdateDate = now,
                UpdateUserId = CurrentUser.Id
            });
    }
}
