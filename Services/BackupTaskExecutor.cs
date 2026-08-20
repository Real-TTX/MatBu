using System.Text.Json;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed record SecondaryExportPayload(GatewaySourceRequest Source, long JobId, BackupConsistencySettings Consistency);
public sealed record SecondaryImportPayload(GatewayTargetRequest Target, long JobId, long TotalBytes, string Sha256);
public sealed record SecondaryStreamingImportPayload(GatewayTargetRequest Target, long JobId);
public sealed record SecondaryLocalStreamingPayload(GatewaySourceRequest Source, GatewayTargetRequest Target, long JobId, BackupConsistencySettings Consistency);
public sealed record SecondaryLocalStreamingResult(GatewayArchiveMetrics Metrics, string Destination);

public sealed class BackupTaskExecutor(
    PersistentStore store,
    ArchiveService archiveService,
    GatewayTransferService transfers,
    SmbClientService smbClient,
    SecondaryCommandService commands,
    IncrementalSourceService incrementalSources,
    ReverseIncrementalRepositoryService incrementalRepository,
    BackupRetentionService retentionService,
    DockerConsistencyService dockerConsistency,
    ProxmoxNativeBackupService proxmoxNative,
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
            if (task.Method == BackupMethod.ProxmoxNative)
            {
                await ExecuteProxmoxNativeAsync(task, source, target, sourceInstance, targetInstance, job, route, cancellationToken);
                return;
            }

            if (BackupMethodPolicy.IsChunked(task.Method))
            {
                await ExecuteReverseIncrementalAsync(task, source, target, sourceInstance, targetInstance, job, route, cancellationToken);
                return;
            }

            if (task.Method == BackupMethod.Full &&
                sourceInstance.Role == InstanceRole.Secondary &&
                targetInstance.Role == InstanceRole.Primary)
            {
                await ExecuteStreamedFullFromSecondaryAsync(task, source, target, sourceInstance, targetInstance, job, route, cancellationToken);
                return;
            }

            if (task.Method == BackupMethod.Full &&
                sourceInstance.Role == InstanceRole.Secondary &&
                targetInstance.Role == InstanceRole.Secondary &&
                sourceInstance.Id != targetInstance.Id)
            {
                await ExecuteStreamedFullAcrossSecondariesAsync(task, source, target, sourceInstance, targetInstance, job, route, cancellationToken);
                return;
            }

            if (task.Method == BackupMethod.Full &&
                sourceInstance.Role == InstanceRole.Secondary &&
                targetInstance.Role == InstanceRole.Secondary &&
                sourceInstance.Id == targetInstance.Id)
            {
                await ExecuteStreamedFullOnSameSecondaryAsync(task, source, target, sourceInstance, job, route, cancellationToken);
                return;
            }

            var totalBytes = await EnsureSourceArchiveAsync(task, source, sourceInstance, job, cachePath, partialPath, cancellationToken);
            MarkPhase(job.Id, JobPhase.Integrity);
            var archiveSha256 = await ArchiveIntegrity.ComputeSha256Async(cachePath, cancellationToken);
            MarkArchiveIntegrity(job.Id, archiveSha256);
            AppendStep(job.Id, "Integrität", "Completed", $"Quellarchiv mit SHA-256 {archiveSha256} verifiziert.", "Primary", cachePath, totalBytes, totalBytes);
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
                destination = await UploadToSecondaryAsync(task, target, targetInstance, job, cachePath, totalBytes, archiveSha256, cancellationToken);
            }
            else
            {
                destination = await StoreOnPrimaryAsync(task, target, job, cachePath, totalBytes, archiveSha256, cancellationToken);
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
        catch (OperationCanceledException) when (ReadJob(job.Id).CancelRequested)
        {
            // User-initiated cancel — the persisted flag is authoritative and covers both cancellation
            // sources (the per-job linked token AND a secondary command reporting "Cancelled", which throws
            // an OCE from WaitForCompletionAsync possibly before the 1s worker watcher trips the token).
            await CancelJobAsync(task, job, cachePath, partialPath);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppendStep(job.Id, "Abschluss", "Cancelled", "Backup wurde durch das Beenden der Instanz unterbrochen und bleibt fortsetzbar.", "Primary", partialPath);
            MarkTask(task.Id, "Geplant");
            throw;
        }
        catch (Exception ex)
        {
            // If the user requested a cancel, a non-OCE error thrown in the same window is still a cancel —
            // finalize as "Abgebrochen" instead of "Fehler"+retry (which would leave the flag set, self-cancel
            // the retry, and emit a spurious failure notification).
            if (ReadJob(job.Id).CancelRequested)
            {
                await CancelJobAsync(task, job, cachePath, partialPath);
                return;
            }
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

    private async Task ExecuteProxmoxNativeAsync(
        BackupTask task,
        BackupObject source,
        BackupObject target,
        MatBuInstance sourceInstance,
        MatBuInstance targetInstance,
        TransferJob job,
        string route,
        CancellationToken cancellationToken)
    {
        if (source.Kind != ObjectKind.Proxmox || target.Kind != ObjectKind.ProxmoxBackupServer)
            throw new InvalidOperationException("Proxmox Native benötigt Proxmox VE als Quelle und Proxmox Backup Server als Ziel.");
        if (sourceInstance.Id != targetInstance.Id)
            throw new InvalidOperationException("PVE und PBS müssen für einen nativen Job derselben MatBu-Instanz zugeordnet sein.");

        var sourceCredential = store.GetSmbCredential(source.Id);
        var targetCredential = store.GetSmbCredential(target.Id);
        var request = new ProxmoxNativeBackupRequest(
            source.Location,
            sourceCredential?.Username,
            sourceCredential?.Password,
            target.Location,
            targetCredential?.Username,
            targetCredential?.Password,
            SourceSelection.Parse(task.SourceSelectionJson),
            job.Id);
        AppendStep(job.Id, "Proxmox Native", "Started", "PVE überträgt die gewählten Gäste direkt und inkrementell in den PBS-Datastore.", sourceInstance.Name, target.Location);

        ProxmoxNativeBackupResult result;
        if (sourceInstance.Role == InstanceRole.Secondary)
        {
            var commandId = commands.Queue(sourceInstance.Id, SecondaryCommandKind.CreateProxmoxNativeBackup, job.TransferId, request);
            AppendStep(job.Id, "Gateway", "Queued", $"Nativer PBS-Auftrag #{commandId} wurde über die ausgehende Secondary-Verbindung bereitgestellt.", sourceInstance.Name, source.Location);
            var command = await commands.WaitForCompletionAsync(commandId, TimeSpan.FromMinutes(15), cancellationToken);
            if (command.State != "Completed") throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte den nativen PBS-Job nicht abschließen." : command.Error);
            result = JsonSerializer.Deserialize<ProxmoxNativeBackupResult>(command.ResultJson)
                ?? throw new InvalidDataException("Die Secondary lieferte kein PBS-Ergebnis.");
        }
        else
        {
            result = await proxmoxNative.ExecuteAsync(
                request,
                _ =>
                {
                    MarkJob(job.Id, "Running", 0, null, "PVE → PBS", speed: 0);
                    return Task.CompletedTask;
                },
                cancellationToken);
        }

        foreach (var snapshot in result.Snapshots)
            AppendStep(job.Id, "PBS Snapshot", "Completed", $"{snapshot.GuestType.ToUpperInvariant()} {snapshot.GuestId} '{snapshot.GuestName}' wurde als {snapshot.SnapshotPath} katalogisiert.", targetInstance.Name, result.Destination, snapshot.Size, snapshot.Size);

        RecordNativeSnapshot(task, job, result);
        MarkTask(task.Id, "Gesichert");
        MarkJob(job.Id, "Completed", result.TotalBytes, result.TotalBytes, result.Destination, speed: 0, resolvedDestination: result.Destination);
        await ApplyRetentionSafelyAsync(task, target, targetInstance, job.Id, cancellationToken);
        AppendStep(job.Id, "Abschluss", "Completed", $"Proxmox-Native-Backup erfolgreich. Weg: {route}. PVE schrieb direkt nach PBS.", $"{sourceInstance.Name} → PBS", result.Destination, result.TotalBytes, result.TotalBytes);
    }

    private void RecordNativeSnapshot(BackupTask task, TransferJob job, ProxmoxNativeBackupResult result)
    {
        store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            var snapshotId = store.NextId(data.BackupSnapshots.Select(item => item.Id));
            data.BackupSnapshots.Add(new BackupSnapshot
            {
                Id = snapshotId,
                TaskId = task.Id,
                TransferJobId = job.Id,
                Token = Guid.NewGuid().ToString("N"),
                Method = BackupMethod.ProxmoxNative,
                State = "Completed",
                RootPath = result.Destination,
                ManifestPath = JsonSerializer.Serialize(result.Snapshots),
                FileCount = result.Snapshots.Count,
                TotalBytes = result.TotalBytes,
                StoredBytes = result.TotalBytes,
                CreateDate = now,
                UpdateDate = now
            });
            var currentJob = data.TransferJobs.First(item => item.Id == job.Id);
            currentJob.Method = BackupMethod.ProxmoxNative;
            currentJob.SnapshotId = snapshotId;
            currentJob.SourceBytes = result.TotalBytes;
            currentJob.StoredBytes = result.TotalBytes;
            currentJob.UpdateDate = now;
        });
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

    private async Task ExecuteStreamedFullOnSameSecondaryAsync(
        BackupTask task,
        BackupObject source,
        BackupObject target,
        MatBuInstance instance,
        TransferJob job,
        string route,
        CancellationToken cancellationToken)
    {
        var transferId = EnsureTransferId(job);
        var sourceCredential = store.GetSmbCredential(source.Id);
        var targetCredential = store.GetSmbCredential(target.Id);
        var sourceRequest = new GatewaySourceRequest(
            transferId,
            source.Kind,
            source.Location,
            sourceCredential?.Username,
            sourceCredential?.Password,
            Compression: job.Compression,
            IncludedPaths: SourceSelection.Parse(job.SourceSelectionJson));
        var targetRequest = new GatewayTargetRequest(
            task.Id,
            target.Kind,
            target.Location,
            targetCredential?.Username,
            targetCredential?.Password,
            task.Compression);
        var payload = new SecondaryLocalStreamingPayload(sourceRequest, targetRequest, job.Id, BackupConsistencySettings.FromTask(task));

        AppendStep(job.Id, "Streaming", "Started", "Die Secondary liest die Quelle und schreibt jeden fertigen Block direkt ins Ziel; die Primary koordiniert und protokolliert.", instance.Name, target.Location);
        var commandId = commands.Queue(instance.Id, SecondaryCommandKind.StreamSourceToTarget, transferId, payload);
        var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
        if (command.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte die lokale Streaming-Pipeline nicht abschliessen." : command.Error);

        var result = JsonSerializer.Deserialize<SecondaryLocalStreamingResult>(command.ResultJson)
            ?? throw new InvalidDataException("Die Secondary lieferte kein Streaming-Ergebnis.");
        CompleteStreamedJob(job.Id, result.Metrics.SourceBytes, result.Metrics.StoredBytes, result.Destination, result.Metrics.Sha256);
        AppendStep(job.Id, "Quelle", "Completed", $"{result.Metrics.SourceBytes:N0} Bytes auf '{instance.Name}' gelesen.", instance.Name, source.Location, result.Metrics.SourceBytes, result.Metrics.SourceBytes);
        AppendStep(job.Id, "Ziel", "Completed", $"{result.Metrics.StoredBytes:N0} Bytes waehrend der Quellaufnahme nach '{result.Destination}' geschrieben.", instance.Name, result.Destination, result.Metrics.StoredBytes, result.Metrics.StoredBytes);
        MarkTask(task.Id, "Gesichert");
        await ApplyRetentionSafelyAsync(task, target, instance, job.Id, cancellationToken);
        AppendStep(job.Id, "Abschluss", "Completed", $"Streaming-Backup erfolgreich. Weg: {route}. Tatsaechliches Ziel: {result.Destination}.", instance.Name, result.Destination, result.Metrics.StoredBytes, result.Metrics.StoredBytes);
    }

    private async Task ExecuteStreamedFullAcrossSecondariesAsync(
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
        var sourceCredential = store.GetSmbCredential(source.Id);
        var targetCredential = store.GetSmbCredential(target.Id);
        var sourceRequest = new GatewaySourceRequest(
            transferId,
            source.Kind,
            source.Location,
            sourceCredential?.Username,
            sourceCredential?.Password,
            Compression: job.Compression,
            IncludedPaths: SourceSelection.Parse(job.SourceSelectionJson));
        var targetRequest = new GatewayTargetRequest(
            task.Id,
            target.Kind,
            target.Location,
            targetCredential?.Username,
            targetCredential?.Password,
            task.Compression);

        AppendStep(job.Id, "Streaming", "Started", "Quelle, Primary-Relay und Ziel laufen als gemeinsame Pipeline mit fortsetzbaren Checkpoints.", $"{sourceInstance.Name} -> Primary -> {targetInstance.Name}", target.Location);
        var targetCommandId = commands.Queue(targetInstance.Id, SecondaryCommandKind.ImportStreamingTarget, transferId, new SecondaryStreamingImportPayload(targetRequest, job.Id));
        var sourceCommandId = commands.Queue(sourceInstance.Id, SecondaryCommandKind.ExportSource, transferId, new SecondaryExportPayload(sourceRequest, job.Id, BackupConsistencySettings.FromTask(task)));
        AppendStep(job.Id, "Gateway", "Queued", $"Source-Kommando #{sourceCommandId} und Target-Kommando #{targetCommandId} wurden parallel bereitgestellt.", $"{sourceInstance.Name} -> {targetInstance.Name}", transferId);

        var sourceCommand = await commands.WaitForCompletionAsync(sourceCommandId, cancellationToken);
        if (sourceCommand.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(sourceCommand.Error) ? "Die Source-Secondary konnte den Stream nicht bereitstellen." : sourceCommand.Error);
        var targetCommand = await commands.WaitForCompletionAsync(targetCommandId, cancellationToken);
        if (targetCommand.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(targetCommand.Error) ? "Die Target-Secondary konnte den Stream nicht schreiben." : targetCommand.Error);

        var metrics = JsonSerializer.Deserialize<GatewayArchiveMetrics>(sourceCommand.ResultJson)
            ?? new GatewayArchiveMetrics(0, sourceCommand.BytesTransferred);
        var destination = string.IsNullOrWhiteSpace(targetCommand.ResultJson)
            ? target.Location
            : JsonSerializer.Deserialize<string>(targetCommand.ResultJson) ?? targetCommand.ResultJson.Trim('"');
        var total = metrics.StoredBytes > 0 ? metrics.StoredBytes : Math.Max(sourceCommand.BytesTransferred, targetCommand.BytesTransferred);
        CompleteStreamedJob(job.Id, metrics.SourceBytes, total, destination, metrics.Sha256);

        AppendStep(job.Id, "Quelle", "Completed", $"{metrics.SourceBytes:N0} Bytes auf '{sourceInstance.Name}' gelesen.", sourceInstance.Name, source.Location, metrics.SourceBytes, metrics.SourceBytes);
        AppendStep(job.Id, "Gateway", "Completed", $"{total:N0} Bytes firewall-freundlich ueber die Primary weitergeleitet.", $"{sourceInstance.Name} -> Primary -> {targetInstance.Name}", transferId, total, total);
        AppendStep(job.Id, "Ziel", "Completed", $"{total:N0} Bytes fortlaufend nach '{destination}' geschrieben.", targetInstance.Name, destination, total, total);
        MarkTask(task.Id, "Gesichert");
        await ApplyRetentionSafelyAsync(task, target, targetInstance, job.Id, cancellationToken);
        AppendStep(job.Id, "Abschluss", "Completed", $"Streaming-Backup erfolgreich. Weg: {route}. Tatsaechliches Ziel: {destination}.", $"{sourceInstance.Name} -> {targetInstance.Name}", destination, total, total);
        transfers.CleanupSourceArtifacts(transferId);
    }

    private async Task ExecuteStreamedFullFromSecondaryAsync(
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
        var credential = store.GetSmbCredential(source.Id);
        var request = new GatewaySourceRequest(
            transferId,
            source.Kind,
            source.Location,
            credential?.Username,
            credential?.Password,
            Compression: job.Compression,
            IncludedPaths: SourceSelection.Parse(job.SourceSelectionJson));
        var consistency = BackupConsistencySettings.FromTask(task);

        AppendStep(job.Id, "Streaming", "Started", "Quelle lesen, zur Primary uebertragen und ins Ziel schreiben laufen als gemeinsame Pipeline.", $"{sourceInstance.Name} -> {targetInstance.Name}", target.Location);
        AppendStep(job.Id, "Quelle", "Started", $"Quell-Object '{source.Name}' wird auf '{sourceInstance.Name}' fortlaufend archiviert.", sourceInstance.Name, source.Location);
        AppendStep(job.Id, "Ziel", "Started", $"Eintreffende Bloecke werden sofort in '{target.Name}' geschrieben.", targetInstance.Name, target.Location);

        var commandId = commands.Queue(sourceInstance.Id, SecondaryCommandKind.ExportSource, transferId, new SecondaryExportPayload(request, job.Id, consistency));
        AppendStep(job.Id, "Gateway", "Queued", $"Streaming-Kommando #{commandId} wurde ueber die ausgehende Secondary-Verbindung bereitgestellt.", sourceInstance.Name, source.Location);
        if (job.ConsistencyMode != BackupConsistencyMode.None)
            AppendStep(job.Id, "Konsistenz", "Queued", $"Konsistenzmodus {ConsistencyLabel(job.ConsistencyMode)} wird direkt um die Quellaufnahme ausgefuehrt.", sourceInstance.Name, job.ConsistencyContainerNames);

        var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
        if (command.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte die Streaming-Pipeline nicht abschliessen." : command.Error);

        var metrics = JsonSerializer.Deserialize<GatewayArchiveMetrics>(command.ResultJson)
            ?? new GatewayArchiveMetrics(0, command.BytesTransferred);
        var completed = ReadJob(job.Id);
        var total = metrics.StoredBytes > 0 ? metrics.StoredBytes : Math.Max(completed.BytesTransferred, command.BytesTransferred);
        var destination = string.IsNullOrWhiteSpace(completed.ResolvedDestination) ? target.Location : completed.ResolvedDestination;
        CompleteStreamedJob(job.Id, metrics.SourceBytes, total, destination, metrics.Sha256);

        if (job.ConsistencyMode != BackupConsistencyMode.None)
            AppendStep(job.Id, "Konsistenz", "Completed", "Die Secondary hat die Anwendung nach der fortlaufenden Quellaufnahme wieder freigegeben.", sourceInstance.Name, job.ConsistencyContainerNames);
        AppendStep(job.Id, "Quelle", "Completed", $"{metrics.SourceBytes:N0} Bytes gelesen und fortlaufend archiviert.", sourceInstance.Name, source.Location, metrics.SourceBytes, metrics.SourceBytes);
        AppendStep(job.Id, "Gateway", "Completed", $"{total:N0} Bytes uebertragen; kein vollstaendiges Vorab-Archiv war erforderlich.", $"{sourceInstance.Name} -> Primary", transferId, total, total);
        AppendStep(job.Id, "Ziel", "Completed", $"{total:N0} Bytes wurden waehrend der Uebertragung nach '{destination}' geschrieben.", targetInstance.Name, destination, total, total);

        MarkTask(task.Id, "Gesichert");
        await ApplyRetentionSafelyAsync(task, target, targetInstance, job.Id, cancellationToken);
        AppendStep(job.Id, "Abschluss", "Completed", $"Streaming-Backup erfolgreich. Weg: {route}. Tatsaechliches Ziel: {destination}.", $"{sourceInstance.Name} -> {targetInstance.Name}", destination, total, total);
        transfers.CleanupSourceArtifacts(transferId);
    }

    private void CompleteStreamedJob(long jobId, long read, long transferred, string destination, string sha256)
    {
        store.Update(data =>
        {
            var job = data.TransferJobs.First(item => item.Id == jobId);
            job.State = "Completed";
            job.Phase = JobPhase.Completed;
            job.BytesRead = read;
            job.BytesTransferred = transferred;
            job.BytesWritten = transferred;
            job.TotalBytes = transferred;
            job.SourceBytes = read;
            job.StoredBytes = transferred;
            job.EstimatedStoredBytes = transferred;
            job.ReadSpeedBytesPerSecond = 0;
            job.SpeedBytesPerSecond = 0;
            job.WriteSpeedBytesPerSecond = 0;
            job.CheckpointPath = destination;
            job.ResolvedDestination = destination;
            if (ArchiveIntegrity.IsSha256(sha256)) job.ArchiveSha256 = sha256.ToLowerInvariant();
            job.UpdateDate = DateTimeOffset.UtcNow;
        });
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
        string archiveSha256,
        CancellationToken cancellationToken)
    {
        if (target.Kind == ObjectKind.LocalFolder)
        {
            Directory.CreateDirectory(target.Location);
            var destination = Path.Combine(target.Location, $"task-{task.Id}-{job.Id}{ArchiveExtension(task.Compression)}");
            await CopyFileResumableAsync(archivePath, destination, job.Id, totalBytes, archiveSha256, cancellationToken);
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
        string archiveSha256,
        CancellationToken cancellationToken)
    {
        var transferId = EnsureTransferId(job);
        var credential = target.Kind == ObjectKind.Smb ? store.GetSmbCredential(target.Id) : null;
        var targetRequest = new GatewayTargetRequest(task.Id, target.Kind, target.Location, credential?.Username, credential?.Password, task.Compression, archiveSha256);
        var commandId = commands.Queue(instance.Id, SecondaryCommandKind.ImportTarget, transferId, new SecondaryImportPayload(targetRequest, job.Id, totalBytes, archiveSha256));
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
        MarkPhase(job.Id, JobPhase.ConsistencyPause);
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
            if (state is "Gesichert" or "Fehler" or "Abgebrochen")
            {
                task.LastRun = DateTimeOffset.UtcNow;
                task.NextRetryDate = null;
            }
        });
    }

    private async Task CancelJobAsync(BackupTask task, TransferJob job, string cachePath, string partialPath)
    {
        var current = ReadJob(job.Id);
        if (Guid.TryParse(current.TransferId, out _))
        {
            try { await transfers.CancelTransferAsync(job.Id, current.TransferId, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Cancel cleanup of transfer artifacts failed for job {JobId}", job.Id); }
            if (BackupMethodPolicy.IsChunked(task.Method))
                TryDeleteDirectory(incrementalSources.TransferDirectory(current.TransferId));
        }
        TryDelete(cachePath);
        TryDelete(partialPath);
        MarkJob(job.Id, "Abgebrochen", current.BytesTransferred, current.TotalBytes, cachePath, speed: 0);
        store.Update(data =>
        {
            var stored = data.TransferJobs.FirstOrDefault(item => item.Id == job.Id);
            if (stored is null) return;
            stored.Phase = JobPhase.Cancelled;
            stored.CancelRequested = false;
            stored.UpdateDate = DateTimeOffset.UtcNow;
        });
        MarkTask(task.Id, "Abgebrochen");
        AppendStep(job.Id, "Abschluss", "Cancelled", "Backup wurde auf Benutzerwunsch abgebrochen; Teilartefakte wurden entfernt.", "Primary", cachePath);
        logger.LogInformation("Task {TaskId} ({TaskName}) job {JobId} was cancelled by the user", task.Id, task.Name, job.Id);
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
            job.Phase = state switch
            {
                "Completed" => JobPhase.Completed,
                "Fehler" or "Failed" => JobPhase.Failed,
                "Abgebrochen" => JobPhase.Cancelled,
                _ => job.Phase
            };
            job.BytesTransferred = bytes;
            if (total is not null) job.TotalBytes = total.Value;
            job.CheckpointPath = checkpoint;
            job.Error = error ?? job.Error;
            if (speed is not null) job.SpeedBytesPerSecond = speed.Value;
            if (!string.IsNullOrWhiteSpace(resolvedDestination)) job.ResolvedDestination = resolvedDestination;
            job.UpdateDate = DateTimeOffset.UtcNow;
        });
    }

    private void MarkWriteProgress(long jobId, long written, long total, string checkpoint, long speed)
    {
        store.Update(data =>
        {
            var job = data.TransferJobs.FirstOrDefault(current => current.Id == jobId);
            if (job is null) return;
            job.State = "Running";
            job.Phase = JobPhase.Writing;
            job.BytesTransferred = written;
            if (total > 0) job.TotalBytes = total;
            job.BytesWritten = written;
            job.WriteSpeedBytesPerSecond = speed;
            job.SpeedBytesPerSecond = speed;
            job.CheckpointPath = checkpoint;
            job.UpdateDate = DateTimeOffset.UtcNow;
        });
    }

    private void MarkPhase(long jobId, string phase)
    {
        store.Update(data =>
        {
            var job = data.TransferJobs.FirstOrDefault(current => current.Id == jobId);
            if (job is null) return;
            job.Phase = phase;
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
            if (job.Phase != JobPhase.ReadPausedSlowTransfer) job.Phase = JobPhase.Reading;
            job.BytesTransferred = progress.SourceBytes;
            job.TotalBytes = progress.EstimatedSourceBytes;
            job.BytesRead = progress.SourceBytes;
            job.ReadSpeedBytesPerSecond = progress.SpeedBytesPerSecond;
            job.EstimatedSourceBytes = progress.EstimatedSourceBytes;
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

    private void MarkArchiveIntegrity(long jobId, string sha256)
    {
        store.Update(data =>
        {
            var job = data.TransferJobs.FirstOrDefault(current => current.Id == jobId);
            if (job is null) return;
            job.ArchiveSha256 = sha256.ToLowerInvariant();
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
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var partialPath = destinationPath + ".partial";
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length == totalBytes)
        {
            try
            {
                await ArchiveIntegrity.VerifySha256Async(destinationPath, expectedSha256, cancellationToken);
                return;
            }
            catch (InvalidDataException) { File.Delete(destinationPath); }
        }
        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (offset > totalBytes)
        {
            File.Delete(partialPath);
            offset = 0;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(partialPath, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            input.Position = offset;
            var buffer = new byte[4 * 1024 * 1024];
            var window = new SpeedWindow();
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                offset += read;
                MarkWriteProgress(jobId, offset, totalBytes, partialPath, window.Sample(offset));
            }
            await output.FlushAsync(cancellationToken);
        }
        File.Move(partialPath, destinationPath, overwrite: true);
        await ArchiveIntegrity.VerifySha256Async(destinationPath, expectedSha256, cancellationToken);
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
