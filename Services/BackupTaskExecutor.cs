using System.Text.Json;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed record SecondaryExportPayload(GatewaySourceRequest Source, long JobId, BackupConsistencySettings Consistency);
public sealed record SecondaryImportPayload(GatewayTargetRequest Target, long JobId, long TotalBytes);

public sealed class BackupTaskExecutor(
    PersistentStore store,
    ArchiveService archiveService,
    SmbClientService smbClient,
    SecondaryCommandService commands,
    IncrementalSourceService incrementalSources,
    ReverseIncrementalRepositoryService incrementalRepository,
    BackupRetentionService retentionService,
    DockerConsistencyService dockerConsistency,
    ILogger<BackupTaskExecutor> logger)
{
    public async Task ExecuteAsync(BackupTask task, CancellationToken cancellationToken)
    {
        var data = store.Read();
        var source = data.Objects.FirstOrDefault(item => item.Id == task.SourceId);
        var target = data.Objects.FirstOrDefault(item => item.Id == task.TargetId);
        var sourceInstance = source is null ? null : data.Instances.FirstOrDefault(instance => instance.Id == source.InstanceId);
        var targetInstance = target is null ? null : data.Instances.FirstOrDefault(instance => instance.Id == target.InstanceId);

        if (source is null || target is null)
        {
            MarkTask(task.Id, "Fehler");
            return;
        }

        if (sourceInstance is null || targetInstance is null)
        {
            MarkTask(task.Id, "Fehler");
            return;
        }

        var start = StartOrResumeJob(task, source, target, sourceInstance, targetInstance);
        var job = ReadJob(start.JobId);
        var cachePath = Path.Combine(archiveService.CacheDirectory, $"task-{task.Id}-{job.TransferId}.archive");
        var partialPath = cachePath + ".partial";
        var route = $"{source.Name} auf {sourceInstance.Name} → {target.Name} auf {targetInstance.Name}";

        AppendStep(
            job.Id,
            "Start",
            start.Resumed ? "Resumed" : "Started",
            start.Resumed ? $"Job wird als Versuch {job.Attempt} vom Checkpoint fortgesetzt." : $"Backup-Job wurde gestartet: {route}.",
            "Primary",
            cachePath);
        AppendStep(
            job.Id,
            "Route",
            "Info",
            $"Quelle '{source.Name}' ({source.Kind}) unter '{source.Location}' auf Instanz '{sourceInstance.Name}' → Ziel '{target.Name}' ({target.Kind}) unter '{target.Location}' auf Instanz '{targetInstance.Name}'.",
            $"{sourceInstance.Name} → {targetInstance.Name}",
            target.Location);
        var selectedSourcePaths = SourceSelection.Parse(task.SourceSelectionJson);
        AppendStep(
            job.Id,
            "Auswahl",
            "Info",
            selectedSourcePaths.Count == 0 ? "Das vollständige Quell-Object wird gesichert." : $"Es werden {selectedSourcePaths.Count} ausgewählte Quellordner gesichert: {string.Join(", ", selectedSourcePaths)}.",
            sourceInstance.Name,
            source.Location);
        MarkTask(task.Id, "Läuft");

        try
        {
            if (BackupMethodPolicy.IsChunked(task.Method))
            {
                await ExecuteReverseIncrementalAsync(task, source, target, sourceInstance, targetInstance, job, route, cancellationToken);
                return;
            }

            var totalBytes = await EnsureSourceArchiveAsync(task, source, sourceInstance, job, cachePath, partialPath, cancellationToken);
            MarkJob(job.Id, "Running", 0, totalBytes, cachePath, speed: 0);

            AppendStep(
                job.Id,
                "Ziel",
                "Started",
                $"Archiv wird an Ziel '{target.Name}' auf Instanz '{targetInstance.Name}' geschrieben.",
                targetInstance.Name,
                target.Location,
                0,
                totalBytes);

            string destination;
            if (targetInstance.Role == InstanceRole.Secondary)
            {
                destination = await UploadToSecondaryAsync(task, target, targetInstance, job, cachePath, totalBytes, cancellationToken);
            }
            else
            {
                destination = await StoreOnPrimaryAsync(task, target, job, cachePath, totalBytes, cancellationToken);
            }

            AppendStep(
                job.Id,
                "Ziel",
                "Completed",
                $"Archiv wurde erfolgreich nach '{destination}' geschrieben.",
                targetInstance.Name,
                destination,
                totalBytes,
                totalBytes);
            MarkTask(task.Id, "Gesichert");
            MarkJob(job.Id, "Completed", totalBytes, totalBytes, destination, speed: 0, resolvedDestination: destination);
            await ApplyRetentionSafelyAsync(task, target, targetInstance, job.Id, cancellationToken);
            AppendStep(
                job.Id,
                "Abschluss",
                "Completed",
                $"Backup erfolgreich abgeschlossen. Weg: {route}. Tatsächliches Ziel: {destination}.",
                $"{sourceInstance.Name} → {targetInstance.Name}",
                destination,
                totalBytes,
                totalBytes);
            TryDelete(cachePath);
            TryDelete(partialPath);
            logger.LogInformation(
                "Task {TaskId} ({TaskName}) completed via {SourceInstance} -> {TargetInstance}; destination {Destination}",
                task.Id,
                task.Name,
                sourceInstance.Name,
                targetInstance.Name,
                destination);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppendStep(job.Id, "Abschluss", "Cancelled", "Backup wurde durch das Beenden der Instanz unterbrochen und bleibt fortsetzbar.", "Primary", partialPath);
            MarkTask(task.Id, "Geplant");
            throw;
        }
        catch (Exception ex)
        {
            var currentJob = ReadJob(job.Id);
            var incrementalCheckpoint = string.IsNullOrWhiteSpace(currentJob.TransferId)
                ? string.Empty
                : incrementalSources.TransferDirectory(currentJob.TransferId);
            var checkpoint = BackupMethodPolicy.IsChunked(task.Method)
                ? incrementalCheckpoint
                : File.Exists(partialPath) ? partialPath : cachePath;
            var bytes = BackupMethodPolicy.IsChunked(task.Method)
                ? currentJob.BytesTransferred
                : File.Exists(checkpoint) ? new FileInfo(checkpoint).Length : 0;
            MarkJob(job.Id, "Fehler", bytes, currentJob.TotalBytes, checkpoint, ex.Message);
            AppendStep(job.Id, "Fehler", "Failed", ex.Message, "Primary", checkpoint, bytes, currentJob.TotalBytes);
            var retry = ScheduleRetry(task.Id, currentJob.Attempt);
            AppendStep(
                job.Id,
                "Retry",
                retry.NextRetryDate is null ? "Exhausted" : "Queued",
                retry.NextRetryDate is null
                    ? $"Keine automatische Wiederholung mehr: {retry.Attempt} von {retry.MaxAttempts} Versuchen wurden verwendet."
                    : $"Automatische Wiederaufnahme als Versuch {retry.Attempt + 1} von {retry.MaxAttempts} am {retry.NextRetryDate.Value.ToLocalTime():dd.MM.yyyy HH:mm:ss}.",
                "Primary",
                checkpoint,
                bytes,
                currentJob.TotalBytes);
            logger.LogWarning(ex, "Task {TaskId} ({TaskName}) failed; retry will use the retained checkpoint", task.Id, task.Name);
        }
    }

    private async Task ExecuteReverseIncrementalAsync(
        BackupTask task,
        BackupObject source,
        BackupObject target,
        MatBuInstance sourceInstance,
        MatBuInstance targetInstance,
        TransferJob job,
        string route,
        CancellationToken cancellationToken)
    {
        var transferId = EnsureTransferId(job);
        var repositoryKey = incrementalRepository.BuildRepositoryKey(task, target, targetInstance);
        var previous = await incrementalRepository.LoadPreviousManifestAsync(task.Token, cancellationToken);
        var baseline = task.Method == BackupMethod.Differential
            ? await incrementalRepository.LoadBaselineManifestAsync(task.Token, cancellationToken)
            : null;
        var comparison = task.Method == BackupMethod.Differential ? baseline : previous;

        AppendStep(
            job.Id,
            "Quelle",
            "Started",
            $"Quelle wird mit {task.ChunkSizeMiB} MiB großen SHA-256-Blöcken katalogisiert.",
            sourceInstance.Name,
            source.Location);
        var selectedPaths = SourceSelection.Parse(task.SourceSelectionJson);

        IncrementalSourcePreparation preparation;
        if (sourceInstance.Role == InstanceRole.Secondary)
        {
            preparation = await PrepareIncrementalFromSecondaryAsync(task, source, sourceInstance, job, repositoryKey, cancellationToken);
        }
        else
        {
            preparation = await incrementalSources.PrepareAsync(
                source,
                store.GetSmbCredential(source.Id),
                task.Token,
                transferId,
                task.ChunkSizeMiB,
                cancellationToken,
                selectedPaths);
            preparation.Manifest.Method = task.Method;
            preparation.Manifest.ParentSnapshotToken = previous?.SnapshotToken ?? "";
            preparation.Manifest.BaselineSnapshotToken = baseline?.SnapshotToken ?? comparison?.SnapshotToken ?? preparation.Manifest.SnapshotToken;
            preparation.Manifest.ChainDepth = previous is null ? 0 : previous.ChainDepth + 1;
            incrementalSources.ApplyPreviousManifest(preparation.Manifest, comparison, repositoryKey);
            if (task.Method == BackupMethod.Differential)
                IncrementalSourceService.MarkChunksNeededForTransition(preparation.Manifest, previous);
            await IncrementalManifestJson.WriteAsync(preparation.ManifestPath, preparation.Manifest, cancellationToken);
        }

        var manifest = preparation.Manifest;
        MarkJob(job.Id, "Running", manifest.StoredBytes, manifest.TotalBytes, preparation.WorkingDirectory, speed: 0);
        AppendStep(
            job.Id,
            "Quelle",
            "Completed",
            $"{manifest.Files.Count:N0} Dateien katalogisiert: {manifest.TotalBytes:N0} Bytes logisch, {manifest.StoredBytes:N0} Bytes geändert, {manifest.ReusedBytes:N0} Bytes wiederverwendet.",
            sourceInstance.Name,
            preparation.ManifestPath,
            manifest.StoredBytes,
            manifest.TotalBytes);

        AppendStep(
            job.Id,
            BackupMethodPolicy.Label(task.Method),
            "Started",
            "Geänderte Blöcke werden geprüft und der neue Plain-Current-Stand atomar veröffentlicht.",
            targetInstance.Name,
            target.Location,
            0,
            manifest.StoredBytes);

        IncrementalApplyResult result;
        if (targetInstance.Role == InstanceRole.Secondary)
        {
            result = await ApplyIncrementalOnSecondaryAsync(task, target, targetInstance, job, manifest, repositoryKey, cancellationToken);
            await incrementalRepository.SaveCatalogAndRecordAsync(task, job, manifest, result, cancellationToken);
        }
        else
        {
            result = await incrementalRepository.ApplyAsync(
                task,
                target,
                job,
                manifest,
                previous,
                store.GetSmbCredential(target.Id),
                cancellationToken);
        }

        MarkTask(task.Id, "Gesichert");
        MarkJob(job.Id, "Completed", result.TotalBytes, result.TotalBytes, result.RepositoryManifestPath, speed: 0, resolvedDestination: result.Destination);
        await ApplyRetentionSafelyAsync(task, target, targetInstance, job.Id, cancellationToken);
        AppendStep(
            job.Id,
            BackupMethodPolicy.Label(task.Method),
            "Completed",
            $"Plain Current aktualisiert. Übertragen/gespeichert: {result.StoredBytes:N0} Bytes; wiederverwendet: {result.ReusedBytes:N0} Bytes.",
            targetInstance.Name,
            result.Destination,
            result.TotalBytes,
            result.TotalBytes);
        AppendStep(
            job.Id,
            "Abschluss",
            "Completed",
            $"{BackupMethodPolicy.Label(task.Method)}-Backup erfolgreich. Weg: {route}. Snapshot {manifest.SnapshotToken}.",
            $"{sourceInstance.Name} → {targetInstance.Name}",
            result.Destination,
            result.TotalBytes,
            result.TotalBytes);
        TryDeleteDirectory(preparation.WorkingDirectory);
    }

    private async Task<IncrementalSourcePreparation> PrepareIncrementalFromSecondaryAsync(
        BackupTask task,
        BackupObject source,
        MatBuInstance instance,
        TransferJob job,
        string repositoryKey,
        CancellationToken cancellationToken)
    {
        var transferId = EnsureTransferId(job);
        var credential = store.GetSmbCredential(source.Id);
        var request = new GatewaySourceRequest(transferId, source.Kind, source.Location, credential?.Username, credential?.Password, IncludedPaths: SourceSelection.Parse(task.SourceSelectionJson));
        var payload = new IncrementalSourceCommandPayload(task.Id, task.Token, task.ChunkSizeMiB, request, job.Id, repositoryKey, task.Method);
        var commandId = commands.Queue(instance.Id, SecondaryCommandKind.PrepareIncrementalSource, transferId, payload);
        AppendStep(
            job.Id,
            "Gateway",
            "Queued",
            $"Incremental-Scan #{commandId} wurde für '{instance.Name}' bereitgestellt; nur fehlende SHA-256-Chunks werden zur Primary gesendet.",
            instance.Name,
            source.Location);
        var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
        if (command.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte die Incremental-Quelle nicht vorbereiten." : command.Error);

        var manifestPath = incrementalSources.ManifestPath(transferId);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Die Secondary meldete Erfolg, aber das Incremental-Manifest fehlt auf der Primary.", manifestPath);
        var manifest = await IncrementalManifestJson.ReadAsync(manifestPath, cancellationToken);
        await incrementalSources.VerifyChangedChunksAsync(manifest, transferId, cancellationToken);
        AppendStep(
            job.Id,
            "Gateway",
            "Completed",
            $"Incremental-Scan #{commandId}: {manifest.StoredBytes:N0} geänderte Bytes wurden hashgeprüft empfangen.",
            $"{instance.Name} → Primary",
            incrementalSources.TransferDirectory(transferId),
            manifest.StoredBytes,
            manifest.TotalBytes);
        return new IncrementalSourcePreparation(
            manifest,
            incrementalSources.TransferDirectory(transferId),
            incrementalSources.ChunkDirectory(transferId),
            manifestPath);
    }

    private async Task<IncrementalApplyResult> ApplyIncrementalOnSecondaryAsync(
        BackupTask task,
        BackupObject target,
        MatBuInstance instance,
        TransferJob job,
        IncrementalBackupManifest manifest,
        string repositoryKey,
        CancellationToken cancellationToken)
    {
        var credential = store.GetSmbCredential(target.Id);
        var targetRequest = new GatewayTargetRequest(task.Id, target.Kind, target.Location, credential?.Username, credential?.Password);
        var payload = new IncrementalTargetCommandPayload(task.Id, task.Token, targetRequest, job.Id, repositoryKey);
        var commandId = commands.Queue(instance.Id, SecondaryCommandKind.ApplyIncrementalTarget, job.TransferId, payload);
        AppendStep(
            job.Id,
            "Gateway",
            "Queued",
            $"Incremental-Apply #{commandId} wurde für '{instance.Name}' bereitgestellt; die Secondary holt nur geänderte Chunks ausgehend ab.",
            instance.Name,
            target.Location,
            0,
            manifest.StoredBytes);
        var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
        if (command.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte Plain Current nicht aktualisieren." : command.Error);
        var result = JsonSerializer.Deserialize<IncrementalApplyResult>(command.ResultJson, IncrementalManifestJson.Options);
        if (result is null) throw new InvalidDataException("Die Secondary lieferte kein Incremental-Ergebnis.");
        AppendStep(
            job.Id,
            "Gateway",
            "Completed",
            $"Incremental-Apply #{commandId} abgeschlossen; Plain Current liegt auf '{instance.Name}'.",
            $"Primary → {instance.Name}",
            result.Destination,
            result.TotalBytes,
            result.TotalBytes);
        return result;
    }

    private async Task<long> EnsureSourceArchiveAsync(
        BackupTask task,
        BackupObject source,
        MatBuInstance sourceInstance,
        TransferJob job,
        string cachePath,
        string partialPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(cachePath))
        {
            var cachedLength = new FileInfo(cachePath).Length;
            AppendStep(job.Id, "Quelle", "Resumed", "Vorhandenes Quellarchiv wird als Checkpoint wiederverwendet.", "Primary", cachePath, cachedLength, cachedLength);
            return cachedLength;
        }

        AppendStep(
            job.Id,
            "Quelle",
            "Started",
            $"Quell-Object '{source.Name}' wird auf Instanz '{sourceInstance.Name}' archiviert.",
            sourceInstance.Name,
            source.Location);

        if (sourceInstance.Role == InstanceRole.Secondary)
        {
            await DownloadFromSecondaryAsync(task, source, sourceInstance, job, partialPath, cancellationToken);
            File.Move(partialPath, cachePath, overwrite: true);
            var secondaryLength = new FileInfo(cachePath).Length;
            AppendStep(
                job.Id,
                "Quelle",
                "Completed",
                $"Quellarchiv wurde auf '{sourceInstance.Name}' erstellt und zur Primary übertragen.",
                $"{sourceInstance.Name} → Primary",
                cachePath,
                secondaryLength,
                secondaryLength);
            return secondaryLength;
        }

        TryDelete(partialPath);
        var buildingPath = partialPath + ".building";
        TryDelete(buildingPath);
        var result = await CreateLocalSourceArchiveAsync(task, source, sourceInstance, job, buildingPath, cancellationToken);
        File.Move(buildingPath, cachePath, overwrite: true);
        var length = new FileInfo(cachePath).Length;
        MarkArchiveMetrics(job.Id, result.SourceBytes, result.StoredBytes);
        AppendStep(job.Id, "Quelle", "Completed", "Quellarchiv wurde auf der Primary erstellt.", sourceInstance.Name, cachePath, length, length);
        return length;
    }

    private async Task DownloadFromSecondaryAsync(
        BackupTask task,
        BackupObject source,
        MatBuInstance instance,
        TransferJob job,
        string partialPath,
        CancellationToken cancellationToken)
    {
        var transferId = EnsureTransferId(job);
        var credential = store.GetSmbCredential(source.Id);
        var request = new GatewaySourceRequest(transferId, source.Kind, source.Location, credential?.Username, credential?.Password, Compression: job.Compression, IncludedPaths: SourceSelection.Parse(job.SourceSelectionJson));
        var consistency = BackupConsistencySettings.FromTask(task);
        var commandId = commands.Queue(instance.Id, SecondaryCommandKind.ExportSource, transferId, new SecondaryExportPayload(request, job.Id, consistency));
        AppendStep(
            job.Id,
            "Gateway",
            "Queued",
            $"Export-Kommando #{commandId} wurde für Secondary '{instance.Name}' bereitgestellt. Die Secondary holt es über ihre ausgehende Verbindung ab.",
            instance.Name,
            source.Location);
        if (job.ConsistencyMode != BackupConsistencyMode.None)
            AppendStep(job.Id, "Konsistenz", "Queued", $"Konsistenzmodus {ConsistencyLabel(job.ConsistencyMode)} wird auf Secondary '{instance.Name}' direkt um die Quellaufnahme ausgeführt.", instance.Name, job.ConsistencyContainerNames);

        var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
        if (command.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte die Quelle nicht exportieren." : command.Error);
        if (job.ConsistencyMode != BackupConsistencyMode.None)
            AppendStep(job.Id, "Konsistenz", "Completed", "Die Secondary hat die Anwendung nach der Quellaufnahme wieder freigegeben.", instance.Name, job.ConsistencyContainerNames);

        var metrics = JsonSerializer.Deserialize<GatewayArchiveMetrics>(command.ResultJson);

        var incoming = Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.tar");
        if (!File.Exists(incoming))
            throw new FileNotFoundException("Die Secondary meldete Erfolg, aber das Source-Archiv fehlt auf der Primary.", incoming);
        TryDelete(partialPath);
        File.Move(incoming, partialPath, overwrite: true);
        var length = new FileInfo(partialPath).Length;
        MarkArchiveMetrics(job.Id, metrics?.SourceBytes ?? 0, metrics?.StoredBytes ?? length);
        MarkJob(job.Id, "Running", length, length, partialPath, speed: command.SpeedBytesPerSecond);
        AppendStep(
            job.Id,
            "Gateway",
            "Completed",
            $"Secondary-Kommando #{commandId} abgeschlossen; {length:N0} Bytes wurden zur Primary übertragen.",
            $"{instance.Name} → Primary",
            partialPath,
            length,
            length);
    }

    private async Task<string> StoreOnPrimaryAsync(
        BackupTask task,
        BackupObject target,
        TransferJob job,
        string archivePath,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        if (target.Kind == ObjectKind.LocalFolder)
        {
            Directory.CreateDirectory(target.Location);
            var destination = Path.Combine(target.Location, $"task-{task.Id}-{job.Id}{ArchiveExtension(task.Compression)}");
            await CopyFileResumableAsync(archivePath, destination, job.Id, totalBytes, cancellationToken);
            return destination;
        }

        if (target.Kind == ObjectKind.Smb)
        {
            var fileName = $"task-{task.Id}-{job.Id}{ArchiveExtension(task.Compression)}";
            await smbClient.UploadFileAsync(target.Location, archivePath, fileName, store.GetSmbCredential(target.Id), cancellationToken);
            var destination = $"{target.Location.TrimEnd('\\', '/')}/{fileName}";
            MarkJob(job.Id, "Running", totalBytes, totalBytes, destination, speed: 0);
            return destination;
        }

        throw new InvalidOperationException($"Der Ziel-Object-Typ {target.Kind} wird auf der Primary noch nicht unterstützt.");
    }

    private async Task<string> UploadToSecondaryAsync(
        BackupTask task,
        BackupObject target,
        MatBuInstance instance,
        TransferJob job,
        string archivePath,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var transferId = EnsureTransferId(job);
        var credential = target.Kind == ObjectKind.Smb ? store.GetSmbCredential(target.Id) : null;
        var targetRequest = new GatewayTargetRequest(task.Id, target.Kind, target.Location, credential?.Username, credential?.Password, task.Compression);
        var commandId = commands.Queue(instance.Id, SecondaryCommandKind.ImportTarget, transferId, new SecondaryImportPayload(targetRequest, job.Id, totalBytes));
        AppendStep(
            job.Id,
            "Gateway",
            "Queued",
            $"Import-Kommando #{commandId} wurde für Secondary '{instance.Name}' bereitgestellt. Die Secondary lädt das Archiv über ihre ausgehende Verbindung.",
            instance.Name,
            target.Location,
            0,
            totalBytes);

        var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
        if (command.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte das Ziel nicht schreiben." : command.Error);

        var destination = string.IsNullOrWhiteSpace(command.ResultJson)
            ? $"Secondary: {target.Location}"
            : JsonSerializer.Deserialize<string>(command.ResultJson) ?? command.ResultJson.Trim('"');
        AppendStep(
            job.Id,
            "Gateway",
            "Completed",
            $"Secondary-Kommando #{commandId} abgeschlossen; Archiv wurde von der Primary an '{instance.Name}' übertragen.",
            $"Primary → {instance.Name}",
            destination,
            totalBytes,
            totalBytes);
        return destination;
    }

    private JobStartResult StartOrResumeJob(
        BackupTask task,
        BackupObject source,
        BackupObject target,
        MatBuInstance sourceInstance,
        MatBuInstance targetInstance)
    {
        long id = 0;
        var resumed = false;
        store.Update(data =>
        {
            var previous = data.TransferJobs
                .Where(job => job.TaskId == task.Id)
                .OrderByDescending(job => job.Id)
                .FirstOrDefault();
            if (previous?.State == "Fehler" && !string.IsNullOrWhiteSpace(previous.TransferId))
            {
                id = previous.Id;
                resumed = true;
                previous.Attempt = Math.Max(1, previous.Attempt) + 1;
                previous.State = "Running";
                previous.Error = "";
                previous.UpdateDate = DateTimeOffset.UtcNow;
                ApplyRouteSnapshot(previous, task, source, target, sourceInstance, targetInstance, JobLabelSnapshots.Create(data, task.Id));
                return;
            }

            id = store.NextId(data.TransferJobs.Select(job => job.Id));
            var now = DateTimeOffset.UtcNow;
            var job = new TransferJob
            {
                Id = id,
                TaskId = task.Id,
                TransferId = Guid.NewGuid().ToString("N"),
                Attempt = 1,
                State = "Running",
                CreateDate = now,
                UpdateDate = now
            };
            ApplyRouteSnapshot(job, task, source, target, sourceInstance, targetInstance, JobLabelSnapshots.Create(data, task.Id));
            data.TransferJobs.Add(job);
        });
        return new JobStartResult(id, resumed);
    }

    private static void ApplyRouteSnapshot(
        TransferJob job,
        BackupTask task,
        BackupObject source,
        BackupObject target,
        MatBuInstance sourceInstance,
        MatBuInstance targetInstance,
        string labelSnapshotJson)
    {
        job.TaskName = task.Name;
        job.LabelSnapshotJson = labelSnapshotJson;
        job.TaskToken = task.Token;
        job.Method = task.Method;
        job.Compression = task.Method == BackupMethod.Full ? task.Compression : BackupCompression.None;
        job.ConsistencyMode = task.ConsistencyMode;
        job.ConsistencyContainerNames = task.ConsistencyContainerNames;
        job.ConsistencyTimeoutSeconds = task.ConsistencyTimeoutSeconds;
        job.SourceSelectionJson = task.SourceSelectionJson;
        job.SourceObjectId = source.Id;
        job.SourceObjectName = source.Name;
        job.SourceObjectKind = source.Kind.ToString();
        job.SourceLocation = source.Location;
        job.SourceInstanceId = sourceInstance.Id;
        job.SourceInstanceName = sourceInstance.Name;
        job.TargetObjectId = target.Id;
        job.TargetObjectName = target.Name;
        job.TargetObjectKind = target.Kind.ToString();
        job.TargetLocation = target.Location;
        job.TargetInstanceId = targetInstance.Id;
        job.TargetInstanceName = targetInstance.Name;
    }

    private TransferJob ReadJob(long id) => store.Read().TransferJobs.First(job => job.Id == id);
    private string EnsureTransferId(TransferJob job) => ReadJob(job.Id).TransferId;

    private async Task<ArchiveCreationResult> CreateLocalSourceArchiveAsync(
        BackupTask task,
        BackupObject source,
        MatBuInstance sourceInstance,
        TransferJob job,
        string buildingPath,
        CancellationToken cancellationToken)
    {
        async Task<ArchiveCreationResult> CreateAsync() => await archiveService.CreateCompressedAsync(
            source,
            store.GetSmbCredential(source.Id),
            buildingPath,
            task.Compression,
            progress => MarkArchiveProgress(job.Id, progress, buildingPath),
            cancellationToken,
            SourceSelection.Parse(task.SourceSelectionJson));

        if (task.ConsistencyMode == BackupConsistencyMode.None) return await CreateAsync();

        var settings = BackupConsistencySettings.FromTask(task);
        AppendStep(job.Id, "Konsistenz", "Started", $"{ConsistencyLabel(task.ConsistencyMode)} wird vor der Quellaufnahme aktiviert.", sourceInstance.Name, task.ConsistencyContainerNames);
        var lease = await dockerConsistency.BeginAsync(settings, cancellationToken);
        AppendStep(job.Id, "Konsistenz", "Active", "Anwendung ist für die konsistente Quellaufnahme vorbereitet.", sourceInstance.Name, task.ConsistencyContainerNames);
        try { return await CreateAsync(); }
        finally
        {
            try
            {
                await dockerConsistency.EndAsync(settings, lease, CancellationToken.None);
                AppendStep(job.Id, "Konsistenz", "Completed", "Anwendung wurde nach der Quellaufnahme wieder freigegeben.", sourceInstance.Name, task.ConsistencyContainerNames);
            }
            catch (Exception exception)
            {
                AppendStep(job.Id, "Konsistenz", "Failed", $"Freigabe nach der Quellaufnahme fehlgeschlagen: {exception.Message}", sourceInstance.Name, task.ConsistencyContainerNames);
                throw new InvalidOperationException("Die Anwendung konnte nach der Quellaufnahme nicht sicher freigegeben werden.", exception);
            }
        }
    }

    private static string ConsistencyLabel(BackupConsistencyMode mode) => mode switch
    {
        BackupConsistencyMode.DockerPause => "Docker Pause",
        BackupConsistencyMode.DockerExec => "Docker Pre-/Post-Hook",
        _ => "Crash-konsistente Aufnahme"
    };

    private void MarkTask(long taskId, string state)
    {
        store.Update(data =>
        {
            var task = data.Tasks.FirstOrDefault(current => current.Id == taskId);
            if (task is null) return;
            task.State = state;
            task.UpdateDate = DateTimeOffset.UtcNow;
            if (state is "Gesichert" or "Fehler")
            {
                task.LastRun = DateTimeOffset.UtcNow;
                task.NextRetryDate = null;
            }
        });
    }

    private RetryScheduleResult ScheduleRetry(long taskId, int attempt)
    {
        var result = new RetryScheduleResult(Math.Max(1, attempt), 1, null);
        store.Update(data =>
        {
            var task = data.Tasks.FirstOrDefault(current => current.Id == taskId);
            if (task is null) return;
            var now = DateTimeOffset.UtcNow;
            var maxAttempts = Math.Clamp(task.MaxRetryAttempts, 1, 20);
            var currentAttempt = Math.Max(1, attempt);
            task.LastRun = now;
            task.UpdateDate = now;
            if (currentAttempt >= maxAttempts)
            {
                task.State = "Fehler";
                task.NextRetryDate = null;
                result = new RetryScheduleResult(currentAttempt, maxAttempts, null);
                return;
            }

            var exponent = Math.Min(10, currentAttempt - 1);
            var delayMinutes = Math.Min(1440, Math.Clamp(task.RetryDelayMinutes, 1, 1440) * (1 << exponent));
            task.NextRetryDate = now.AddMinutes(delayMinutes);
            task.State = BackupScheduler.RetryWaitingState;
            result = new RetryScheduleResult(currentAttempt, maxAttempts, task.NextRetryDate);
        });
        return result;
    }

    private void MarkJob(
        long jobId,
        string state,
        long bytes,
        long? total,
        string checkpoint,
        string? error = null,
        long? speed = null,
        string? resolvedDestination = null)
    {
        store.Update(data =>
        {
            var job = data.TransferJobs.FirstOrDefault(current => current.Id == jobId);
            if (job is null) return;
            job.State = state;
            job.BytesTransferred = bytes;
            if (total is not null) job.TotalBytes = total.Value;
            job.CheckpointPath = checkpoint;
            job.Error = error ?? job.Error;
            if (speed is not null) job.SpeedBytesPerSecond = speed.Value;
            if (!string.IsNullOrWhiteSpace(resolvedDestination)) job.ResolvedDestination = resolvedDestination;
            job.UpdateDate = DateTimeOffset.UtcNow;
        });
    }

    private void MarkArchiveProgress(long jobId, ArchiveProgress progress, string checkpoint)
    {
        store.Update(data =>
        {
            var job = data.TransferJobs.FirstOrDefault(current => current.Id == jobId);
            if (job is null) return;
            job.State = "Running";
            job.BytesTransferred = progress.SourceBytes;
            job.TotalBytes = progress.EstimatedSourceBytes;
            job.SourceBytes = progress.SourceBytes;
            job.StoredBytes = progress.StoredBytes;
            job.EstimatedStoredBytes = progress.EstimatedStoredBytes;
            job.SpeedBytesPerSecond = progress.SpeedBytesPerSecond;
            job.CheckpointPath = checkpoint;
            job.UpdateDate = DateTimeOffset.UtcNow;
        });
    }

    private void MarkArchiveMetrics(long jobId, long sourceBytes, long storedBytes)
    {
        store.Update(data =>
        {
            var job = data.TransferJobs.FirstOrDefault(current => current.Id == jobId);
            if (job is null) return;
            job.SourceBytes = sourceBytes;
            job.StoredBytes = storedBytes;
            job.EstimatedStoredBytes = storedBytes;
            job.UpdateDate = DateTimeOffset.UtcNow;
        });
    }

    private static string ArchiveExtension(BackupCompression compression) => compression == BackupCompression.None ? ".tar" : ".tar.br";

    private void AppendStep(
        long jobId,
        string stage,
        string state,
        string message,
        string instanceName,
        string location,
        long bytesTransferred = 0,
        long totalBytes = 0)
    {
        store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            data.JobSteps.Add(new JobStep
            {
                Id = store.NextId(data.JobSteps.Select(step => step.Id)),
                TransferJobId = jobId,
                Sequence = data.JobSteps.Where(step => step.TransferJobId == jobId).Select(step => step.Sequence).DefaultIfEmpty().Max() + 1,
                Stage = stage,
                State = state,
                Message = message,
                InstanceName = instanceName,
                Location = location,
                BytesTransferred = bytesTransferred,
                TotalBytes = totalBytes,
                CreateDate = now,
                UpdateDate = now
            });
        });
    }

    private async Task ApplyRetentionSafelyAsync(
        BackupTask task,
        BackupObject target,
        MatBuInstance targetInstance,
        long jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await retentionService.ApplyForTaskAsync(task, target, targetInstance, cancellationToken);
            AppendStep(jobId, "Retention", "Completed", result.Message, targetInstance.Name, target.Location);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppendStep(jobId, "Retention", "Warning", $"Backup ist vollständig, Retention konnte aber nicht abgeschlossen werden: {ex.Message}", targetInstance.Name, target.Location);
            logger.LogWarning(ex, "Retention for task {TaskId} failed after a successful backup", task.Id);
        }
    }

    private async Task CopyFileResumableAsync(
        string sourcePath,
        string destinationPath,
        long jobId,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var partialPath = destinationPath + ".partial";
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length == totalBytes) return;
        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (offset > totalBytes)
        {
            File.Delete(partialPath);
            offset = 0;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(partialPath, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        input.Position = offset;
        var buffer = new byte[4 * 1024 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            offset += read;
            var speed = (long)(offset / Math.Max(.001, started.Elapsed.TotalSeconds));
            MarkJob(jobId, "Running", offset, totalBytes, partialPath, speed: speed);
        }
        await output.FlushAsync(cancellationToken);
        File.Move(partialPath, destinationPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed record JobStartResult(long JobId, bool Resumed);
    private sealed record RetryScheduleResult(int Attempt, int MaxAttempts, DateTimeOffset? NextRetryDate);
}
