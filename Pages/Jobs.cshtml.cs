using System.Globalization;
using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages;

public class JobsModel(PersistentStore store) : AppPageModel(store)
{
    public IReadOnlyList<TransferJob> Items { get; private set; } = [];
    public IReadOnlyDictionary<long, BackupTask> Tasks { get; private set; } = new Dictionary<long, BackupTask>();
    public IReadOnlyDictionary<long, BackupObject> Objects { get; private set; } = new Dictionary<long, BackupObject>();
    public IReadOnlyDictionary<long, MatBuInstance> Instances { get; private set; } = new Dictionary<long, MatBuInstance>();
    public IReadOnlyList<JobLabel> JobLabels { get; private set; } = [];
    public IReadOnlyDictionary<long, IReadOnlyList<JobLabelSnapshot>> CurrentLabelsByTask { get; private set; } = new Dictionary<long, IReadOnlyList<JobLabelSnapshot>>();

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string StatusFilter { get; set; } = "Alle";
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "updated";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public long? LabelId { get; set; }
    public int TotalPages { get; private set; }

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        var data = Store.Read();
        Tasks = data.Tasks.ToDictionary(item => item.Id);
        Objects = data.Objects.ToDictionary(item => item.Id);
        Instances = data.Instances.ToDictionary(item => item.Id);
        JobLabels = data.JobLabels.OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var labelById = data.JobLabels.ToDictionary(label => label.Id);
        CurrentLabelsByTask = data.BackupTaskLabels
            .Where(item => labelById.ContainsKey(item.JobLabelId))
            .GroupBy(item => item.BackupTaskId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<JobLabelSnapshot>)group
                    .Select(item => labelById[item.JobLabelId])
                    .OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(label => new JobLabelSnapshot(label.Id, label.Name, JobLabelSnapshots.NormalizeColor(label.Color)))
                    .ToList());

        var query = data.TransferJobs.AsEnumerable();
        if (LabelId is not null) query = query.Where(job => GetLabels(job).Any(label => label.Id == LabelId));
        if (!string.IsNullOrWhiteSpace(Search))
        {
            query = query.Where(job =>
                GetTaskName(job).Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                GetSourceObjectName(job).Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                GetTargetObjectName(job).Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                GetSourceInstanceName(job).Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                GetTargetInstanceName(job).Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                GetDestination(job).Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                GetLabels(job).Any(label => label.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)));
        }
        if (!string.IsNullOrWhiteSpace(StatusFilter) && StatusFilter != "Alle")
            query = query.Where(job => job.State.Equals(StatusFilter, StringComparison.OrdinalIgnoreCase));

        query = Sort == "task"
            ? query.OrderBy(GetTaskName).ThenByDescending(job => job.UpdateDate)
            : query.OrderByDescending(job => job.UpdateDate);
        var all = query.ToList();
        TotalPages = Math.Max(1, (int)Math.Ceiling(all.Count / 15d));
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        Items = all.Skip((PageNumber - 1) * 15).Take(15).ToList();
        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }

    public IActionResult OnPostRetry(long taskId)
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role == UserRole.User) return Forbid();
        Store.Update(data =>
        {
            var task = data.Tasks.FirstOrDefault(item => item.Id == taskId);
            if (task is null) return;
            task.State = "Geplant";
            task.NextRetryDate = null;
            task.UpdateDate = DateTimeOffset.UtcNow;
            task.UpdateUserId = CurrentUser.Id;
        });
        return RedirectToPage(new { LabelId });
    }

    public string GetTaskName(TransferJob job) => !string.IsNullOrWhiteSpace(job.TaskName)
        ? job.TaskName
        : Tasks.TryGetValue(job.TaskId, out var task) ? task.Name : $"Job #{job.TaskId}";

    public IReadOnlyList<JobLabelSnapshot> GetLabels(TransferJob job)
    {
        var snapshot = JobLabelSnapshots.Parse(job.LabelSnapshotJson);
        if (snapshot.Count > 0) return snapshot;
        return CurrentLabelsByTask.TryGetValue(job.TaskId, out var current) ? current : [];
    }

    public string GetSourceObjectName(TransferJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.SourceObjectName)) return job.SourceObjectName;
        if (!Tasks.TryGetValue(job.TaskId, out var task)) return "Unbekannte Quelle";
        return Objects.TryGetValue(task.SourceId, out var source) ? source.Name : "Unbekannte Quelle";
    }

    public string GetTargetObjectName(TransferJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.TargetObjectName)) return job.TargetObjectName;
        if (!Tasks.TryGetValue(job.TaskId, out var task)) return "Unbekanntes Ziel";
        return Objects.TryGetValue(task.TargetId, out var target) ? target.Name : "Unbekanntes Ziel";
    }

    public string GetSourceInstanceName(TransferJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.SourceInstanceName)) return job.SourceInstanceName;
        var objectId = job.SourceObjectId != 0 ? job.SourceObjectId : Tasks.TryGetValue(job.TaskId, out var task) ? task.SourceId : 0;
        if (!Objects.TryGetValue(objectId, out var source)) return "Unbekannte Instanz";
        return Instances.TryGetValue(source.InstanceId, out var instance) ? instance.Name : "Unbekannte Instanz";
    }

    public string GetTargetInstanceName(TransferJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.TargetInstanceName)) return job.TargetInstanceName;
        var objectId = job.TargetObjectId != 0 ? job.TargetObjectId : Tasks.TryGetValue(job.TaskId, out var task) ? task.TargetId : 0;
        if (!Objects.TryGetValue(objectId, out var target)) return "Unbekannte Instanz";
        return Instances.TryGetValue(target.InstanceId, out var instance) ? instance.Name : "Unbekannte Instanz";
    }

    public string GetDestination(TransferJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.ResolvedDestination)) return job.ResolvedDestination;
        if (!string.IsNullOrWhiteSpace(job.TargetLocation)) return job.TargetLocation;
        var objectId = job.TargetObjectId != 0 ? job.TargetObjectId : Tasks.TryGetValue(job.TaskId, out var task) ? task.TargetId : 0;
        return Objects.TryGetValue(objectId, out var target) ? target.Location : "—";
    }

    public string GetMethodName(TransferJob job) => job.Method == BackupMethod.ReverseIncremental
        ? "Reverse Incremental"
        : "Full";

    public string GetSnapshotLabel(TransferJob job)
    {
        if (job.SnapshotId <= 0) return "—";
        var snapshotId = Convert.ToString(job.SnapshotId, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(snapshotId) ? "—" : snapshotId;
    }

    public string FormatEfficiency(TransferJob job) => job.SourceBytes <= 0
        ? "—"
        : $"{Math.Clamp(job.ReusedBytes * 100d / job.SourceBytes, 0d, 100d):0.0}%";

    public string FormatBytes(long value)
    {
        if (value < 1024) return $"{value} B";
        if (value < 1024L * 1024) return $"{value / 1024d:0.0} KiB";
        if (value < 1024L * 1024 * 1024) return $"{value / 1024d / 1024d:0.0} MiB";
        if (value < 1024L * 1024 * 1024 * 1024) return $"{value / 1024d / 1024d / 1024d:0.0} GiB";
        return $"{value / 1024d / 1024d / 1024d / 1024d:0.0} TiB";
    }

}
