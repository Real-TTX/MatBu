using System.Globalization;
using MatBu.Data;
using MatBu.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Jobs;

public class DetailsModel(PersistentStore store) : AppPageModel(store)
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    public TransferJob Job { get; private set; } = new();
    public IReadOnlyList<JobStep> Steps { get; private set; } = [];
    public string TaskName { get; private set; } = "";
    public string SourceObjectName { get; private set; } = "";
    public string SourceKind { get; private set; } = "";
    public string SourceLocation { get; private set; } = "";
    public string SourceInstanceName { get; private set; } = "";
    public string TargetObjectName { get; private set; } = "";
    public string TargetKind { get; private set; } = "";
    public string TargetLocation { get; private set; } = "";
    public string TargetInstanceName { get; private set; } = "";
    public string ActualDestination { get; private set; } = "—";
    public string MethodName => Job.Method == BackupMethod.ReverseIncremental ? "Reverse Incremental" : "Full";
    public string SnapshotLabel
    {
        get
        {
            if (Job.SnapshotId <= 0) return "—";
            var snapshotId = Convert.ToString(Job.SnapshotId, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(snapshotId) ? "—" : snapshotId;
        }
    }
    public string Efficiency => Job.SourceBytes <= 0
        ? "—"
        : $"{Math.Clamp(Job.ReusedBytes * 100d / Job.SourceBytes, 0d, 100d):0.0}%";

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        var data = Store.Read();
        var job = data.TransferJobs.FirstOrDefault(item => item.Id == Id);
        if (job is null) return NotFound();
        Job = job;
        Steps = data.JobSteps.Where(step => step.TransferJobId == Id).OrderBy(step => step.Sequence).ToList();

        var task = data.Tasks.FirstOrDefault(item => item.Id == job.TaskId);
        var sourceId = job.SourceObjectId != 0 ? job.SourceObjectId : task?.SourceId ?? 0;
        var targetId = job.TargetObjectId != 0 ? job.TargetObjectId : task?.TargetId ?? 0;
        var source = data.Objects.FirstOrDefault(item => item.Id == sourceId);
        var target = data.Objects.FirstOrDefault(item => item.Id == targetId);
        var sourceInstanceId = job.SourceInstanceId != 0 ? job.SourceInstanceId : source?.InstanceId ?? 0;
        var targetInstanceId = job.TargetInstanceId != 0 ? job.TargetInstanceId : target?.InstanceId ?? 0;

        TaskName = ValueOr(job.TaskName, task?.Name, $"Job #{job.TaskId}");
        SourceObjectName = ValueOr(job.SourceObjectName, source?.Name, "Unbekannte Quelle");
        SourceKind = ValueOr(job.SourceObjectKind, source?.Kind.ToString(), "—");
        SourceLocation = ValueOr(job.SourceLocation, source?.Location, "—");
        SourceInstanceName = ValueOr(job.SourceInstanceName, data.Instances.FirstOrDefault(item => item.Id == sourceInstanceId)?.Name, "Unbekannte Instanz");
        TargetObjectName = ValueOr(job.TargetObjectName, target?.Name, "Unbekanntes Ziel");
        TargetKind = ValueOr(job.TargetObjectKind, target?.Kind.ToString(), "—");
        TargetLocation = ValueOr(job.TargetLocation, target?.Location, "—");
        TargetInstanceName = ValueOr(job.TargetInstanceName, data.Instances.FirstOrDefault(item => item.Id == targetInstanceId)?.Name, "Unbekannte Instanz");
        ActualDestination = !string.IsNullOrWhiteSpace(job.ResolvedDestination)
            ? job.ResolvedDestination
            : job.State == "Completed" && !string.IsNullOrWhiteSpace(job.CheckpointPath) ? job.CheckpointPath : "Noch nicht geschrieben";
        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }

    private static string ValueOr(string first, string? second, string fallback) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : fallback;

    public string FormatBytes(long value)
    {
        if (value < 1024) return $"{value} B";
        if (value < 1024L * 1024) return $"{value / 1024d:0.0} KiB";
        if (value < 1024L * 1024 * 1024) return $"{value / 1024d / 1024d:0.0} MiB";
        if (value < 1024L * 1024 * 1024 * 1024) return $"{value / 1024d / 1024d / 1024d:0.0} GiB";
        return $"{value / 1024d / 1024d / 1024d / 1024d:0.0} TiB";
    }
}
