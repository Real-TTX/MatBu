using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages;

public sealed record DashboardActivityDay(
    DateOnly Date,
    string Label,
    int CompletedBackups,
    int CompletedRestores,
    int FailedJobs,
    long TransferredBytes)
{
    public int Total => CompletedBackups + CompletedRestores + FailedJobs;
}

public class IndexModel(PersistentStore store) : AppPageModel(store)
{
    public IReadOnlyList<BackupObject> Objects { get; private set; } = [];
    public IReadOnlyList<BackupTask> Tasks { get; private set; } = [];
    public IReadOnlyList<MatBuInstance> Instances { get; private set; } = [];
    public IReadOnlyList<TransferJob> RunningJobs { get; private set; } = [];
    public IReadOnlyList<TransferJob> RecentJobs { get; private set; } = [];
    public IReadOnlyList<DashboardActivityDay> ActivityDays { get; private set; } = [];
    public IReadOnlyDictionary<long, long> LatestVersionIds { get; private set; } = new Dictionary<long, long>();
    public IReadOnlyDictionary<long, BackupStorageMetrics> StorageMetrics { get; private set; } = new Dictionary<long, BackupStorageMetrics>();
    public int HealthyObjects { get; private set; }
    public int AttentionObjects { get; private set; }
    public int CompletedBackups24Hours { get; private set; }
    public int FailedJobs24Hours { get; private set; }
    public int TasksWithErrors { get; private set; }
    public int OnlineInstances { get; private set; }
    public long TransferredBytes24Hours { get; private set; }
    public int MaxActivityValue => Math.Max(1, ActivityDays.Select(day => day.Total).DefaultIfEmpty(0).Max());
    public string Greeting { get; private set; } = "Hallo";
    public TransferJob? LastSuccessfulBackup { get; private set; }
    public bool CanEdit => CurrentUser?.Role != UserRole.User;
    public BackupStorageMetrics GetStorage(long taskId) => StorageMetrics.TryGetValue(taskId, out var metrics)
        ? metrics
        : new BackupStorageMetrics(0, 0, 0);

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        var data = Store.Read();
        var now = DateTimeOffset.Now;
        var cutoff = now.AddHours(-24);

        Objects = data.Objects.OrderBy(item => item.Name).ToList();
        Tasks = data.Tasks.OrderByDescending(item => item.UpdateDate).ToList();
        Instances = data.Instances.OrderBy(item => item.Role).ThenBy(item => item.Name).ToList();
        RunningJobs = data.TransferJobs.Where(item => item.State == "Running").OrderByDescending(item => item.UpdateDate).ToList();
        RecentJobs = data.TransferJobs.OrderByDescending(item => item.UpdateDate).Take(6).ToList();
        LatestVersionIds = data.TransferJobs
            .Where(IsSuccessfulBackup)
            .GroupBy(item => item.TaskId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreateDate).First().Id);
        StorageMetrics = BackupStorageMetricsCalculator.CalculateAll(data);

        HealthyObjects = Objects.Count(item => item.Status == ObjectStatus.Healthy);
        AttentionObjects = Objects.Count - HealthyObjects;
        CompletedBackups24Hours = data.TransferJobs.Count(item => IsSuccessfulBackup(item) && item.UpdateDate >= cutoff);
        FailedJobs24Hours = data.TransferJobs.Count(item => item.State == "Fehler" && item.UpdateDate >= cutoff);
        TasksWithErrors = Tasks.Count(item => item.State == "Fehler");
        var onlineCutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
        OnlineInstances = Instances.Count(item => item.Role == InstanceRole.Primary ||
            (item.Enabled && item.Status == InstanceStatus.Online && item.LastSeenDate >= onlineCutoff));
        TransferredBytes24Hours = data.TransferJobs.Where(item => item.State == "Completed" && item.UpdateDate >= cutoff).Sum(item => item.BytesTransferred);
        LastSuccessfulBackup = data.TransferJobs.Where(IsSuccessfulBackup).OrderByDescending(item => item.UpdateDate).FirstOrDefault();
        ActivityDays = BuildActivity(data.TransferJobs, now);
        Greeting = now.Hour switch { < 11 => "Guten Morgen", < 17 => "Guten Tag", _ => "Guten Abend" };

        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }

    private static IReadOnlyList<DashboardActivityDay> BuildActivity(IEnumerable<TransferJob> jobs, DateTimeOffset now)
    {
        var localJobs = jobs.Select(item => new { Job = item, LocalDate = DateOnly.FromDateTime(item.UpdateDate.ToLocalTime().Date) }).ToList();
        var today = DateOnly.FromDateTime(now.Date);
        var dayNames = new[] { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" };
        var result = new List<DashboardActivityDay>();
        for (var offset = 6; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            var dayJobs = localJobs.Where(item => item.LocalDate == date).Select(item => item.Job).ToList();
            result.Add(new DashboardActivityDay(
                date,
                $"{dayNames[(int)date.DayOfWeek]} {date:dd.MM}",
                dayJobs.Count(IsSuccessfulBackup),
                dayJobs.Count(item => item.State == "Completed" && item.SourceObjectKind == "BackupVersion"),
                dayJobs.Count(item => item.State == "Fehler"),
                dayJobs.Where(item => item.State == "Completed").Sum(item => item.BytesTransferred)));
        }
        return result;
    }

    private static bool IsSuccessfulBackup(TransferJob item) =>
        item.TaskId > 0 &&
        !item.RetentionExpired &&
        item.State == "Completed" &&
        item.SourceObjectKind != "BackupVersion" &&
        !string.IsNullOrWhiteSpace(item.ResolvedDestination);
}
