using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Restore;

public sealed record RestoreVersionItem(long Id, DateTimeOffset CreateDate, string TargetInstanceName, string Destination, long SizeBytes);
public sealed record RestoreBreadcrumb(string Label, string Folder);
public sealed record RestoreTargetItem(long Id, string Name, ObjectKind Kind, string Location, string InstanceName);

public class ExplorerModel(
    PersistentStore store,
    RestoreArchiveService restoreArchives,
    RestoreExecutionService restoreExecution) : AppPageModel(store)
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty(SupportsGet = true)] public long? TaskId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Folder { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty] public long RestoreTargetId { get; set; }
    [BindProperty] public string? RestoreFolderName { get; set; }
    [BindProperty] public List<string> SelectedPaths { get; set; } = [];

    public BackupTask Task { get; private set; } = new();
    public TransferJob Job { get; private set; } = new();
    public IReadOnlyList<RestoreVersionItem> Versions { get; private set; } = [];
    public IReadOnlyList<RestoreTargetItem> RestoreTargets { get; private set; } = [];
    public IReadOnlyList<RestoreBrowserEntry> Entries { get; private set; } = [];
    public IReadOnlyList<RestoreBreadcrumb> Breadcrumbs { get; private set; } = [];
    public string CurrentFolder { get; private set; } = "";
    public string ParentFolder { get; private set; } = "";
    public string? Error { get; private set; }
    public bool HasVersions => Job.Id > 0;
    public string TaskName => HasVersions ? Job.TaskName : Task.Name;
    public bool CanEdit => CurrentUser?.Role != UserRole.User;
    public bool CanRestore => HasVersions && CurrentUser?.Role != UserRole.User;
    public BackupStorageMetrics StorageMetrics { get; private set; } = new(0, 0, 0);
    public long SelectedVersionBytes { get; private set; }
    public string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d / 1024d:0.##} TiB",
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:0.##} GiB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:0.##} MiB",
        >= 1024L => $"{bytes / 1024d:0.0} KiB",
        _ => $"{bytes} Bytes"
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        var data = Store.Read();
        var job = ResolveJob(data);
        var task = ResolveTask(data, job);
        if (task is null && job is null) return NotFound();

        Task = task ?? new BackupTask
        {
            Id = job!.TaskId,
            Token = job.TaskToken,
            Name = job.TaskName
        };
        TaskId = Task.Id;
        StorageMetrics = BackupStorageMetricsCalculator.Calculate(data, Task.Id, Task.Token);
        RestoreTargets = FindRestoreTargets(data);

        if (job is null)
        {
            ViewData["UserName"] = CurrentUser!.UserName;
            return Page();
        }

        Job = job;
        SelectedVersionBytes = BackupStorageMetricsCalculator.VersionSize(data, job);
        Id = job.Id;
        Versions = FindVersions(data, job)
            .Select(item => new RestoreVersionItem(item.Id, item.CreateDate, item.TargetInstanceName, item.ResolvedDestination, BackupStorageMetricsCalculator.VersionSize(data, item)))
            .ToList();
        RestoreTargetId = RestoreTargets.FirstOrDefault()?.Id ?? 0;
        RestoreFolderName = RestoreExecutionService.BuildDefaultFolderName(job);

        try
        {
            CurrentFolder = RestoreArchiveService.NormalizeFolder(Folder);
            ParentFolder = CurrentFolder.Contains('/') ? CurrentFolder[..CurrentFolder.LastIndexOf('/')] : "";
            Breadcrumbs = CreateBreadcrumbs(CurrentFolder);
            var entries = await restoreArchives.BrowseAsync(job, CurrentFolder, cancellationToken);
            Entries = string.IsNullOrWhiteSpace(Search)
                ? entries
                : entries.Where(item => item.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }

        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }

    public async Task<IActionResult> OnPostRestoreAsync(CancellationToken cancellationToken)
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role == UserRole.User) return Forbid();
        var data = Store.Read();
        var job = data.TransferJobs.FirstOrDefault(item => item.Id == Id);
        var target = data.Objects.FirstOrDefault(item => item.Id == RestoreTargetId);
        if (job is null) return NotFound();
        if (job.RetentionExpired)
        {
            TempData["RestoreError"] = "Diese Backupversion wurde durch die Retention entfernt und kann nicht mehr wiederhergestellt werden.";
            return RedirectToExplorer(job);
        }
        if (target is null)
        {
            TempData["RestoreError"] = "Das ausgewählte Restore-Ziel wurde nicht gefunden.";
            return RedirectToExplorer(job);
        }

        try
        {
            var result = await restoreExecution.ExecuteAsync(job, target, SelectedPaths, RestoreFolderName, CurrentUser!.Id, cancellationToken);
            TempData["RestoreSuccess"] = $"Restore Job #{result.RestoreJobId} abgeschlossen: {result.FileCount} Datei(en) nach {result.Destination}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TempData["RestoreError"] = ex.Message;
        }

        return RedirectToExplorer(job);
    }

    public async Task<IActionResult> OnGetDownloadAsync(string entryPath, CancellationToken cancellationToken)
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        var job = Store.Read().TransferJobs.FirstOrDefault(item => item.Id == Id && !item.RetentionExpired);
        if (job is null) return NotFound();
        try
        {
            var file = await restoreArchives.OpenFileAsync(job, entryPath, CurrentUser!.Id, cancellationToken);
            return File(file.Stream, "application/octet-stream", file.DownloadName);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            TempData["RestoreError"] = ex.Message;
            return RedirectToExplorer(job);
        }
    }

    private TransferJob? ResolveJob(AppData data)
    {
        if (Id > 0)
            return data.TransferJobs.FirstOrDefault(item => item.Id == Id && IsBackupVersion(item));
        if (TaskId is null or <= 0) return null;
        return data.TransferJobs
            .Where(item => item.TaskId == TaskId && IsBackupVersion(item))
            .OrderByDescending(item => item.CreateDate)
            .FirstOrDefault();
    }

    private BackupTask? ResolveTask(AppData data, TransferJob? job)
    {
        var taskId = TaskId is > 0 ? TaskId.Value : job?.TaskId ?? 0;
        if (taskId > 0)
        {
            var task = data.Tasks.FirstOrDefault(item => item.Id == taskId);
            if (task is not null) return task;
        }

        if (job is null || string.IsNullOrWhiteSpace(job.TaskToken)) return null;
        return data.Tasks.FirstOrDefault(item => item.Token == job.TaskToken);
    }

    private IActionResult RedirectToExplorer(TransferJob job) => RedirectToPage(new
    {
        id = job.Id,
        taskId = job.TaskId,
        folder = Folder
    });

    private static IEnumerable<TransferJob> FindVersions(AppData data, TransferJob job)
    {
        var completed = data.TransferJobs.Where(IsBackupVersion);
        completed = !string.IsNullOrWhiteSpace(job.TaskToken)
            ? completed.Where(item => item.TaskToken == job.TaskToken)
            : completed.Where(item => item.TaskId == job.TaskId && item.TaskName.Equals(job.TaskName, StringComparison.OrdinalIgnoreCase));
        return completed.OrderByDescending(item => item.CreateDate);
    }

    private static bool IsBackupVersion(TransferJob item) =>
        item.TaskId > 0 &&
        !item.RetentionExpired &&
        item.State.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
        !item.SourceObjectKind.Equals("BackupVersion", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(item.ResolvedDestination);

    private static IReadOnlyList<RestoreTargetItem> FindRestoreTargets(AppData data) => data.Objects
        .Where(item => item.Direction != ObjectDirection.Source && item.Kind is ObjectKind.LocalFolder or ObjectKind.DockerVolume)
        .Select(item => new { Object = item, Instance = data.Instances.FirstOrDefault(instance => instance.Id == item.InstanceId) })
        .Where(item => item.Instance is { Enabled: true })
        .OrderBy(item => item.Object.Name)
        .Select(item => new RestoreTargetItem(item.Object.Id, item.Object.Name, item.Object.Kind, item.Object.Location, item.Instance!.Name))
        .ToList();

    private static IReadOnlyList<RestoreBreadcrumb> CreateBreadcrumbs(string folder)
    {
        var items = new List<RestoreBreadcrumb> { new("Backup", "") };
        if (string.IsNullOrEmpty(folder)) return items;
        var current = "";
        foreach (var segment in folder.Split('/'))
        {
            current = string.IsNullOrEmpty(current) ? segment : $"{current}/{segment}";
            items.Add(new RestoreBreadcrumb(segment, current));
        }
        return items;
    }
}
