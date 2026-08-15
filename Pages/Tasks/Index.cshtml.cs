using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Tasks;

public class IndexModel(PersistentStore store) : AppPageModel(store)
{
    public IReadOnlyList<BackupTask> Items { get; private set; } = [];
    public IReadOnlyList<BackupObject> Objects { get; private set; } = [];
    public IReadOnlyDictionary<long, string> InstanceNames { get; private set; } = new Dictionary<long, string>();
    public IReadOnlyDictionary<long, long> LatestRestoreVersionIds { get; private set; } = new Dictionary<long, long>();
    public IReadOnlyDictionary<long, BackupStorageMetrics> StorageMetrics { get; private set; } = new Dictionary<long, BackupStorageMetrics>();
    public IReadOnlyList<JobLabel> JobLabels { get; private set; } = [];
    public IReadOnlyDictionary<long, IReadOnlyList<JobLabel>> LabelsByTask { get; private set; } = new Dictionary<long, IReadOnlyList<JobLabel>>();
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "name";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public long? LabelId { get; set; }
    public int TotalPages { get; private set; }
    public bool CanEdit => CurrentUser?.Role != UserRole.User;
    public string GetInstanceName(long instanceId) => InstanceNames.TryGetValue(instanceId, out var name) ? name : "Unbekannte Instanz";
    public string GetMethodSummary(BackupTask task) => BackupMethodPolicy.IsChunked(task.Method)
        ? $"{BackupMethodPolicy.Label(task.Method)} · {task.ChunkSizeMiB} MiB · {GetSelectionSummary(task)}"
        : $"{BackupMethodPolicy.Label(task.Method)} · {GetSelectionSummary(task)}";
    private static string GetSelectionSummary(BackupTask task)
    {
        var count = SourceSelection.Parse(task.SourceSelectionJson).Count;
        return count == 0 ? "gesamtes Object" : $"{count} Ordner";
    }
    public BackupStorageMetrics GetStorage(long taskId) => StorageMetrics.TryGetValue(taskId, out var metrics)
        ? metrics
        : new BackupStorageMetrics(0, 0, 0);
    public IReadOnlyList<JobLabel> GetLabels(long taskId) => LabelsByTask.TryGetValue(taskId, out var labels) ? labels : [];
    public string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d / 1024d:0.##} TiB",
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:0.##} GiB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:0.##} MiB",
        >= 1024L => $"{bytes / 1024d:0.0} KiB",
        _ => $"{bytes} Bytes"
    };
    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        var data = Store.Read();
        Objects = data.Objects;
        JobLabels = data.JobLabels.OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var labelById = data.JobLabels.ToDictionary(label => label.Id);
        LabelsByTask = data.BackupTaskLabels
            .Where(item => labelById.ContainsKey(item.JobLabelId))
            .GroupBy(item => item.BackupTaskId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<JobLabel>)group.Select(item => labelById[item.JobLabelId]).OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase).ToList());
        StorageMetrics = BackupStorageMetricsCalculator.CalculateAll(data);
        InstanceNames = data.Instances.ToDictionary(x => x.Id, x => x.Name);
        LatestRestoreVersionIds = data.TransferJobs
            .Where(x => x.TaskId > 0 && !x.RetentionExpired && x.State == "Completed" && x.SourceObjectKind != "BackupVersion" && !string.IsNullOrWhiteSpace(x.ResolvedDestination))
            .GroupBy(x => x.TaskId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.CreateDate).First().Id);
        var query = data.Tasks.AsEnumerable();
        if (LabelId is not null)
        {
            var taskIds = data.BackupTaskLabels.Where(item => item.JobLabelId == LabelId).Select(item => item.BackupTaskId).ToHashSet();
            query = query.Where(task => taskIds.Contains(task.Id));
        }
        if (!string.IsNullOrWhiteSpace(Search)) query = query.Where(x => x.Name.Contains(Search, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(StatusFilter) && StatusFilter != "Alle") query = query.Where(x => x.State.Equals(StatusFilter, StringComparison.OrdinalIgnoreCase));
        query = Sort == "updated" ? query.OrderByDescending(x => x.UpdateDate) : query.OrderBy(x => x.Name);
        var all = query.ToList(); TotalPages = Math.Max(1, (int)Math.Ceiling(all.Count / 10d)); PageNumber = Math.Clamp(PageNumber, 1, TotalPages); Items = all.Skip((PageNumber - 1) * 10).Take(10).ToList();
        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }
    public IActionResult OnPostRun(long id)
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (!CanEdit) return Forbid();
        Store.Update(data => { var task = data.Tasks.FirstOrDefault(x => x.Id == id); if (task is not null) { task.State = "Geplant"; task.NextRetryDate = null; task.UpdateDate = DateTimeOffset.UtcNow; } });
        return RedirectToPage("/Tasks/Index", new { LabelId });
    }
    public IActionResult OnPostDelete(long id)
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (!CanEdit) return Forbid();
        Store.Update(data =>
        {
            data.Tasks.RemoveAll(x => x.Id == id);
            data.BackupTaskLabels.RemoveAll(item => item.BackupTaskId == id);
        });
        return RedirectToPage("/Tasks/Index", new { LabelId });
    }
}
