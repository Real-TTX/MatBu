using System.Text.Json;
using System.Text.RegularExpressions;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed record RetentionVersionPlan(
    long JobId,
    BackupMethod Method,
    string ArtifactName,
    string SnapshotToken);

public sealed record RetentionCleanupPayload(
    long TaskId,
    string TaskToken,
    string Retention,
    GatewayTargetRequest Target,
    IReadOnlyList<RetentionVersionPlan> ExpiredVersions,
    IReadOnlyList<string> RetainedSnapshotTokens);

public sealed record RetentionCleanupResult(int ExpiredVersions, int DeletedChunks, string Message);

public sealed partial class BackupRetentionService(
    PersistentStore store,
    SmbClientService smbClient,
    ReverseIncrementalRepositoryService incrementalRepository,
    SecondaryCommandService commands,
    ILogger<BackupRetentionService> logger)
{
    public async Task<RetentionCleanupResult> ApplyForTaskAsync(
        BackupTask task,
        BackupObject target,
        MatBuInstance targetInstance,
        CancellationToken cancellationToken)
    {
        var data = store.Read();
        var routeJobs = data.TransferJobs
            .Where(job => IsVersionForRoute(job, task, target, targetInstance) && !job.RetentionExpired)
            .OrderByDescending(job => job.CreateDate)
            .ToList();
        if (routeJobs.Count <= 1) return EmptyResult(task.Retention);

        var cutoff = GetCutoff(task.Retention, DateTimeOffset.UtcNow);
        var expiredJobs = routeJobs.Skip(1).Where(job => job.CreateDate < cutoff).ToList();
        if (expiredJobs.Count == 0) return EmptyResult(task.Retention);

        var expiredIds = expiredJobs.Select(job => job.Id).ToHashSet();
        var plans = expiredJobs.Select(job => BuildPlan(data, job)).ToList();
        var retainedSnapshotTokens = routeJobs
            .Where(job => !expiredIds.Contains(job.Id) && job.Method == BackupMethod.ReverseIncremental)
            .Select(job => FindSnapshotToken(data, job))
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var credential = target.Kind == ObjectKind.Smb ? store.GetSmbCredential(target.Id) : null;
        RetentionCleanupResult physical;
        if (targetInstance.Role == InstanceRole.Secondary)
        {
            var request = new GatewayTargetRequest(task.Id, target.Kind, target.Location, credential?.Username, credential?.Password);
            var payload = new RetentionCleanupPayload(task.Id, task.Token, task.Retention, request, plans, retainedSnapshotTokens);
            var commandId = commands.Queue(targetInstance.Id, SecondaryCommandKind.ApplyRetention, Guid.NewGuid().ToString("N"), payload);
            var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
            if (command.State != "Completed")
                throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte die Retention nicht anwenden." : command.Error);
            physical = JsonSerializer.Deserialize<RetentionCleanupResult>(command.ResultJson)
                ?? new RetentionCleanupResult(plans.Count, 0, $"{plans.Count} Version(en) auf der Secondary entfernt.");
            await incrementalRepository.DeleteCatalogSnapshotsAsync(task.Token, plans.Select(plan => plan.SnapshotToken), cancellationToken);
        }
        else
        {
            physical = await ApplyPhysicalAsync(task, target, plans, retainedSnapshotTokens, credential, cancellationToken);
        }

        MarkExpired(expiredIds);
        logger.LogInformation("Retention for task {TaskId} expired {Count} versions and deleted {Chunks} chunks", task.Id, physical.ExpiredVersions, physical.DeletedChunks);
        return physical;
    }

    public async Task<RetentionCleanupResult> ApplyPhysicalAsync(
        BackupTask task,
        BackupObject target,
        IReadOnlyList<RetentionVersionPlan> expiredVersions,
        IReadOnlyList<string> retainedSnapshotTokens,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        foreach (var version in expiredVersions.Where(version => version.Method == BackupMethod.Full))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Kind == ObjectKind.LocalFolder) DeleteLocalArtifact(target.Location, version.ArtifactName);
            else if (target.Kind == ObjectKind.Smb) await smbClient.DeleteRelativeFileAsync(target.Location, version.ArtifactName, credential, cancellationToken);
        }

        var reverseTokens = expiredVersions
            .Where(version => version.Method == BackupMethod.ReverseIncremental && !string.IsNullOrWhiteSpace(version.SnapshotToken))
            .Select(version => version.SnapshotToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deletedChunks = reverseTokens.Count == 0
            ? 0
            : await incrementalRepository.ApplyRetentionAsync(task, target, reverseTokens, retainedSnapshotTokens, credential, cancellationToken);
        return new RetentionCleanupResult(
            expiredVersions.Count,
            deletedChunks,
            $"Retention '{task.Retention}': {expiredVersions.Count} Version(en) und {deletedChunks} nicht mehr benötigte Chunk(s) entfernt.");
    }

    public static DateTimeOffset GetCutoff(string? retention, DateTimeOffset now)
    {
        var match = RetentionPattern().Match(retention ?? "");
        if (!match.Success || !int.TryParse(match.Groups["value"].Value, out var value) || value <= 0)
            return now.AddDays(-30);
        var unit = match.Groups["unit"].Value;
        if (unit.StartsWith("Monat", StringComparison.OrdinalIgnoreCase)) return now.AddMonths(-value);
        if (unit.StartsWith("Woche", StringComparison.OrdinalIgnoreCase)) return now.AddDays(-7d * value);
        return now.AddDays(-value);
    }

    private static bool IsVersionForRoute(TransferJob job, BackupTask task, BackupObject target, MatBuInstance instance) =>
        job.TaskId == task.Id &&
        job.State.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
        !job.SourceObjectKind.Equals("BackupVersion", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(job.ResolvedDestination) &&
        (job.TargetObjectId == 0 || job.TargetObjectId == target.Id) &&
        (job.TargetInstanceId == 0 || job.TargetInstanceId == instance.Id);

    private static RetentionVersionPlan BuildPlan(AppData data, TransferJob job) => new(
        job.Id,
        job.Method,
        ExtractFileName(job.ResolvedDestination),
        FindSnapshotToken(data, job));

    private static string FindSnapshotToken(AppData data, TransferJob job) => data.BackupSnapshots
        .FirstOrDefault(snapshot => snapshot.Id == job.SnapshotId || snapshot.TransferJobId == job.Id)?.Token ?? "";

    private static string ExtractFileName(string destination)
    {
        var normalized = destination.TrimEnd('/', '\\').Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', '\r', '\n']) >= 0)
            throw new InvalidDataException("Der Dateiname der abgelaufenen Backupversion ist ungültig.");
        return name;
    }

    private static void DeleteLocalArtifact(string root, string fileName)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, fileName));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Retention-Zieldatei liegt außerhalb des konfigurierten Backupziels.");
        if (File.Exists(candidate)) File.Delete(candidate);
    }

    private void MarkExpired(HashSet<long> expiredJobIds)
    {
        store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var job in data.TransferJobs.Where(job => expiredJobIds.Contains(job.Id)))
            {
                job.RetentionExpired = true;
                job.UpdateDate = now;
            }
            foreach (var snapshot in data.BackupSnapshots.Where(snapshot => expiredJobIds.Contains(snapshot.TransferJobId)))
            {
                snapshot.State = "Expired";
                snapshot.UpdateDate = now;
            }
        });
    }

    private static RetentionCleanupResult EmptyResult(string retention) =>
        new(0, 0, $"Retention '{retention}' geprüft; keine Version ist abgelaufen.");

    [GeneratedRegex(@"(?<value>\d+)\s*(?<unit>Tage?|Wochen?|Monate?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetentionPattern();
}
