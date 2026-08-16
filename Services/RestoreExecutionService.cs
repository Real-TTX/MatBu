using System.Formats.Tar;
using System.Text.Json;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed record SecondaryRestorePayload(
    GatewayTargetRequest Target,
    long RestoreJobId,
    long TotalBytes,
    string RestoreFolderName,
    string Sha256);

public sealed record RestoreExecutionResult(
    long RestoreJobId,
    string Destination,
    int FileCount,
    long RestoredBytes);

public sealed class RestoreExecutionService(
    PersistentStore store,
    RestoreArchiveService restoreArchives,
    ArchiveService archiveService,
    GatewayTransferService transfers,
    SecondaryCommandService commands,
    ILogger<RestoreExecutionService> logger)
{
    public static string BuildDefaultFolderName(TransferJob sourceJob)
    {
        var taskName = SanitizeFolderSegment(sourceJob.TaskName);
        var timestamp = sourceJob.CreateDate.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss");
        return $"Restore {taskName} {timestamp}";
    }

    public async Task<RestoreExecutionResult> ExecuteAsync(
        TransferJob sourceJob,
        BackupObject target,
        IReadOnlyCollection<string> selectedPaths,
        string? requestedFolderName,
        long userId,
        CancellationToken cancellationToken)
    {
        ValidateSourceAndTarget(sourceJob, target);
        var data = store.Read();
        var targetInstance = data.Instances.FirstOrDefault(item => item.Id == target.InstanceId)
            ?? throw new InvalidOperationException("Die Instanz des Restore-Ziels wurde nicht gefunden.");
        if (!targetInstance.Enabled)
            throw new InvalidOperationException($"Die Zielinstanz '{targetInstance.Name}' ist deaktiviert.");

        var normalizedPaths = NormalizeSelectedPaths(selectedPaths);
        if (normalizedPaths.Count == 0)
            throw new InvalidOperationException("Wähle mindestens eine Datei oder einen Ordner für den Restore aus.");

        var restoreFolderName = NormalizeRestoreFolderName(requestedFolderName, sourceJob);
        var transferId = Guid.NewGuid().ToString("N");
        var packagePath = transfers.GetRestorePackagePath(transferId);
        var restoreJobId = CreateRestoreJob(sourceJob, target, targetInstance, transferId, restoreFolderName, normalizedPaths, userId);

        try
        {
            var package = await BuildPackageAsync(sourceJob, normalizedPaths, restoreFolderName, packagePath, cancellationToken);
            var packageSha256 = await ArchiveIntegrity.ComputeSha256Async(packagePath, cancellationToken);
            AppendStep(
                restoreJobId,
                "Restore-Paket",
                "Completed",
                $"{package.FileCount} Datei(en) mit {package.RestoredBytes:N0} Bytes wurden aus Backup Job #{sourceJob.Id} vorbereitet.",
                "Primary",
                packagePath,
                package.PackageBytes,
                package.PackageBytes,
                userId);

            var destination = targetInstance.Role == InstanceRole.Secondary
                ? await ApplyOnSecondaryAsync(restoreJobId, target, targetInstance, transferId, package.PackageBytes, packageSha256, restoreFolderName, cancellationToken)
                : await ApplyOnPrimaryAsync(target, targetInstance, packagePath, restoreFolderName, cancellationToken);

            CompleteRestoreJob(restoreJobId, destination, package.PackageBytes);
            AppendStep(
                restoreJobId,
                "Restore",
                "Completed",
                $"File-Level Restore erfolgreich abgeschlossen. Tatsächliches Ziel: {destination}",
                targetInstance.Name,
                destination,
                package.PackageBytes,
                package.PackageBytes,
                userId);
            AppendStep(
                sourceJob.Id,
                "Restore",
                "Completed",
                $"Restore Job #{restoreJobId}: {package.FileCount} Datei(en) wurden nach '{destination}' wiederhergestellt.",
                targetInstance.Name,
                destination,
                package.RestoredBytes,
                package.RestoredBytes,
                userId);

            return new RestoreExecutionResult(restoreJobId, destination, package.FileCount, package.RestoredBytes);
        }
        catch (Exception ex)
        {
            FailRestoreJob(restoreJobId, ex.Message, File.Exists(packagePath) ? new FileInfo(packagePath).Length : 0);
            AppendStep(restoreJobId, "Restore", "Failed", ex.Message, targetInstance.Name, target.Location, userId: userId);
            logger.LogWarning(ex, "Restore job {RestoreJobId} from source job {SourceJobId} failed", restoreJobId, sourceJob.Id);
            throw;
        }
        finally
        {
            try { if (File.Exists(packagePath)) File.Delete(packagePath); } catch { }
        }
    }

    private async Task<RestorePackageResult> BuildPackageAsync(
        TransferJob sourceJob,
        IReadOnlyList<string> selectedPaths,
        string restoreFolderName,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var sourceArchivePath = await restoreArchives.EnsureArchiveAvailableAsync(sourceJob, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath)) File.Delete(outputPath);

        var fileCount = 0;
        long restoredBytes = 0;
        var writtenDirectories = new HashSet<string>(StringComparer.Ordinal);

        await using (var input = new FileStream(sourceArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var reader = new TarReader(input, leaveOpen: true))
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            WriteDirectory(writer, restoreFolderName, writtenDirectories);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!RestoreArchiveService.TryNormalizeEntryPath(entry.Name, out var entryPath)) continue;
                if (!selectedPaths.Any(selected => entryPath == selected || entryPath.StartsWith(selected + "/", StringComparison.Ordinal))) continue;

                var destinationPath = $"{restoreFolderName}/{entryPath}";
                if (entry.EntryType == TarEntryType.Directory)
                {
                    WriteParentDirectories(writer, destinationPath + "/placeholder", writtenDirectories);
                    WriteDirectory(writer, destinationPath, writtenDirectories);
                    continue;
                }

                if (entry.DataStream is null) continue;
                WriteParentDirectories(writer, destinationPath, writtenDirectories);
                var restoredEntry = new PaxTarEntry(TarEntryType.RegularFile, destinationPath)
                {
                    DataStream = entry.DataStream
                };
                writer.WriteEntry(restoredEntry);
                fileCount++;
                restoredBytes += entry.Length;
            }
        }

        if (fileCount == 0)
        {
            File.Delete(outputPath);
            throw new InvalidOperationException("Die Auswahl enthält in dieser Backupversion keine wiederherstellbaren Dateien.");
        }

        return new RestorePackageResult(fileCount, restoredBytes, new FileInfo(outputPath).Length);
    }

    private async Task<string> ApplyOnPrimaryAsync(
        BackupObject target,
        MatBuInstance instance,
        string packagePath,
        string restoreFolderName,
        CancellationToken cancellationToken)
    {
        await archiveService.ApplyRestoreArchiveAsync(target.Kind, target.Location, packagePath, cancellationToken);
        return FormatDestination(instance.Name, target, restoreFolderName);
    }

    private async Task<string> ApplyOnSecondaryAsync(
        long restoreJobId,
        BackupObject target,
        MatBuInstance instance,
        string transferId,
        long packageBytes,
        string packageSha256,
        string restoreFolderName,
        CancellationToken cancellationToken)
    {
        var payload = new SecondaryRestorePayload(
            new GatewayTargetRequest(restoreJobId, target.Kind, target.Location, null, null),
            restoreJobId,
            packageBytes,
            restoreFolderName,
            packageSha256);
        var commandId = commands.Queue(instance.Id, SecondaryCommandKind.ApplyRestore, transferId, payload);
        AppendStep(
            restoreJobId,
            "Gateway",
            "Queued",
            $"Restore-Kommando #{commandId} wurde für Secondary '{instance.Name}' bereitgestellt. Die Secondary holt das Restore-Paket über ihre ausgehende Verbindung ab.",
            instance.Name,
            target.Location,
            0,
            packageBytes,
            0);

        var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
        if (command.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte den Restore nicht anwenden." : command.Error);

        if (!string.IsNullOrWhiteSpace(command.ResultJson))
        {
            var remoteDestination = JsonSerializer.Deserialize<string>(command.ResultJson) ?? command.ResultJson.Trim('"');
            return $"{instance.Name} · {remoteDestination}";
        }
        return FormatDestination(instance.Name, target, restoreFolderName);
    }

    private long CreateRestoreJob(
        TransferJob sourceJob,
        BackupObject target,
        MatBuInstance targetInstance,
        string transferId,
        string restoreFolderName,
        IReadOnlyCollection<string> selectedPaths,
        long userId)
    {
        long restoreJobId = 0;
        store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            restoreJobId = store.NextId(data.TransferJobs.Select(item => item.Id));
            data.TransferJobs.Add(new TransferJob
            {
                Id = restoreJobId,
                TaskId = 0,
                TaskName = $"Restore · {sourceJob.TaskName}",
                LabelSnapshotJson = sourceJob.LabelSnapshotJson,
                TransferId = transferId,
                State = "Running",
                SourceObjectName = $"Backupversion #{sourceJob.Id}",
                SourceObjectKind = "BackupVersion",
                SourceLocation = sourceJob.ResolvedDestination,
                SourceInstanceId = sourceJob.TargetInstanceId,
                SourceInstanceName = sourceJob.TargetInstanceName,
                TargetObjectId = target.Id,
                TargetObjectName = target.Name,
                TargetObjectKind = target.Kind.ToString(),
                TargetLocation = target.Location,
                TargetInstanceId = targetInstance.Id,
                TargetInstanceName = targetInstance.Name,
                CheckpointPath = restoreFolderName,
                CreateDate = now,
                UpdateDate = now
            });
            data.JobSteps.Add(new JobStep
            {
                Id = store.NextId(data.JobSteps.Select(item => item.Id)),
                TransferJobId = restoreJobId,
                Sequence = 1,
                Stage = "Auswahl",
                State = "Started",
                Message = $"Restore aus Backup Job #{sourceJob.Id}: {selectedPaths.Count} ausgewählte Datei(en)/Ordner nach '{restoreFolderName}'.",
                InstanceName = "Primary",
                Location = string.Join(", ", selectedPaths),
                CreateDate = now,
                CreateUserId = userId,
                UpdateDate = now,
                UpdateUserId = userId
            });
        });
        return restoreJobId;
    }

    private void CompleteRestoreJob(long restoreJobId, string destination, long packageBytes)
    {
        store.Update(data =>
        {
            var job = data.TransferJobs.First(item => item.Id == restoreJobId);
            job.State = "Completed";
            job.BytesTransferred = packageBytes;
            job.TotalBytes = packageBytes;
            job.ResolvedDestination = destination;
            job.CheckpointPath = destination;
            job.UpdateDate = DateTimeOffset.UtcNow;
        });
    }

    private void FailRestoreJob(long restoreJobId, string error, long packageBytes)
    {
        store.Update(data =>
        {
            var job = data.TransferJobs.FirstOrDefault(item => item.Id == restoreJobId);
            if (job is null) return;
            job.State = "Fehler";
            job.Error = error;
            job.BytesTransferred = packageBytes;
            job.TotalBytes = packageBytes;
            job.UpdateDate = DateTimeOffset.UtcNow;
        });
    }

    private void AppendStep(
        long jobId,
        string stage,
        string state,
        string message,
        string instanceName,
        string location,
        long bytesTransferred = 0,
        long totalBytes = 0,
        long userId = 0)
    {
        store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            data.JobSteps.Add(new JobStep
            {
                Id = store.NextId(data.JobSteps.Select(item => item.Id)),
                TransferJobId = jobId,
                Sequence = data.JobSteps.Where(item => item.TransferJobId == jobId).Select(item => item.Sequence).DefaultIfEmpty().Max() + 1,
                Stage = stage,
                State = state,
                Message = message,
                InstanceName = instanceName,
                Location = location,
                BytesTransferred = bytesTransferred,
                TotalBytes = totalBytes,
                CreateDate = now,
                CreateUserId = userId,
                UpdateDate = now,
                UpdateUserId = userId
            });
        });
    }

    private static void ValidateSourceAndTarget(TransferJob sourceJob, BackupObject target)
    {
        if (sourceJob.RetentionExpired)
            throw new InvalidOperationException("Diese Backupversion wurde durch die Retention entfernt und kann nicht mehr wiederhergestellt werden.");
        if (!sourceJob.State.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Nur eine erfolgreich abgeschlossene Backupversion kann wiederhergestellt werden.");
        if (sourceJob.SourceObjectKind.Equals("BackupVersion", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ein Restore-Job ist keine durchsuchbare Backupversion.");
        if (target.Direction == ObjectDirection.Source)
            throw new InvalidOperationException("Das ausgewählte Object darf nicht als Ziel verwendet werden.");
        if (target.Kind is not (ObjectKind.LocalFolder or ObjectKind.DockerVolume))
            throw new InvalidOperationException("File-Level Restore unterstützt derzeit lokale Ordner und Docker-Volumes als Ziel.");
    }

    private static IReadOnlyList<string> NormalizeSelectedPaths(IEnumerable<string> selectedPaths) => selectedPaths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(RestoreArchiveService.NormalizeFolder)
        .Where(path => !string.IsNullOrEmpty(path))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    private static string NormalizeRestoreFolderName(string? requestedFolderName, TransferJob sourceJob)
    {
        var value = string.IsNullOrWhiteSpace(requestedFolderName) ? BuildDefaultFolderName(sourceJob) : requestedFolderName.Trim();
        if (value.Length > 120) throw new InvalidOperationException("Der Restore-Unterordner darf höchstens 120 Zeichen lang sein.");
        if (value is "." or ".." || value.Any(character => character < 32 || "\\/:*?\"<>|".Contains(character)))
            throw new InvalidOperationException("Der Restore-Unterordner enthält ungültige Zeichen.");
        return value.TrimEnd('.', ' ');
    }

    private static string SanitizeFolderSegment(string value)
    {
        var sanitized = new string(value.Select(character => character < 32 || "\\/:*?\"<>|→".Contains(character) ? '-' : character).ToArray());
        sanitized = string.Join(' ', sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "Backup" : sanitized[..Math.Min(60, sanitized.Length)];
    }

    private static void WriteParentDirectories(TarWriter writer, string path, HashSet<string> writtenDirectories)
    {
        var segments = path.Split('/');
        var current = "";
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = string.IsNullOrEmpty(current) ? segments[index] : $"{current}/{segments[index]}";
            WriteDirectory(writer, current, writtenDirectories);
        }
    }

    private static void WriteDirectory(TarWriter writer, string path, HashSet<string> writtenDirectories)
    {
        if (!writtenDirectories.Add(path)) return;
        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, path));
    }

    private static string FormatDestination(string instanceName, BackupObject target, string restoreFolderName) => target.Kind == ObjectKind.DockerVolume
        ? $"{instanceName} · {target.Location}:/{restoreFolderName}"
        : $"{instanceName} · {Path.Combine(target.Location, restoreFolderName)}";

    private sealed record RestorePackageResult(int FileCount, long RestoredBytes, long PackageBytes);
}
