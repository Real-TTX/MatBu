using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatBu.Models;

namespace MatBu.Services;

public sealed record SecondaryCommandCompletion(bool Success, string ResultJson, string Error);

public sealed class SecondaryConnectionWorker(
    IHttpClientFactory httpClientFactory,
    GatewayTransferService transfers,
    ArchiveService archiveService,
    IncrementalSourceService incrementalSources,
    ReverseIncrementalRepositoryService incrementalRepository,
    BackupRetentionService retentionService,
    RestoreArchiveService restoreArchives,
    ObjectConnectivityTester tester,
    SourceBrowserService sourceBrowser,
    ProxmoxNativeBackupService proxmoxNative,
    ILogger<SecondaryConnectionWorker> logger) : BackgroundService
{
    private const int ChunkSize = 4 * 1024 * 1024;
    // Keepalive so the primary's idle watchdog (default 120s) does not kill a long-but-live build/transfer.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(ResolveInt("MATBU_SECONDARY_HEARTBEAT_SECONDS", 10, 2, 60));
    // Upper bound on how long the build may make no observable forward progress before we let the watchdog
    // trip anyway (guards against masking a genuinely frozen source read).
    private static readonly TimeSpan MaxSilentBuildWindow = TimeSpan.FromSeconds(ResolveInt("MATBU_SECONDARY_BUILD_STALL_SECONDS", 1800, 120, 21600));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static int ResolveInt(string variable, int fallback, int min, int max)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(configured, out var value) ? Math.Clamp(value, min, max) : fallback;
    }
    private readonly string _primaryEndpoint = (Environment.GetEnvironmentVariable("MATBU_PRIMARY_ENDPOINT") ?? "").TrimEnd('/');
    private readonly string? _token = Environment.GetEnvironmentVariable("MATBU_INSTANCE_TOKEN");
    // Per-command cancellation, so a 409 stop directive on the /progress back-channel can abort the in-flight command.
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _commandCts = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MATBU_INSTANCE_ROLE"), "Secondary", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(_primaryEndpoint) || string.IsNullOrWhiteSpace(_token))
        {
            logger.LogError("Secondary connection is not configured: MATBU_PRIMARY_ENDPOINT and MATBU_INSTANCE_TOKEN are required");
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        logger.LogInformation("Secondary outbound connection worker started; connecting to {PrimaryEndpoint}", _primaryEndpoint);
        var client = httpClientFactory.CreateClient("PrimaryConnection");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var command = await PollAsync(client, stoppingToken);
                if (command is not null) await HandleAsync(client, command, stoppingToken);
                else await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbound Secondary connection cycle failed; reconnecting");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<SecondaryCommandEnvelope?> PollAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _primaryEndpoint + "/api/secondary/poll");
        request.Headers.Add("X-MatBu-Instance-Token", _token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SecondaryCommandEnvelope>(JsonOptions, cancellationToken);
    }

    private async Task HandleAsync(HttpClient client, SecondaryCommandEnvelope command, CancellationToken appCancellation)
    {
        using var cmdCts = CancellationTokenSource.CreateLinkedTokenSource(appCancellation);
        _commandCts[command.Id] = cmdCts;
        var cancellationToken = cmdCts.Token;
        try
        {
            switch (command.Kind)
            {
                case SecondaryCommandKind.ObjectTest:
                {
                    var request = JsonSerializer.Deserialize<GatewayObjectTestRequest>(command.PayloadJson) ?? throw new InvalidOperationException("Object-Test-Payload fehlt.");
                    var result = await tester.TestAsync(new BackupObject { Kind = request.Kind, Direction = request.Direction, Location = request.Location }, request.SmbUsername, request.SmbPassword, cancellationToken);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(result), "", cancellationToken);
                    break;
                }
                case SecondaryCommandKind.BrowseSource:
                {
                    var request = JsonSerializer.Deserialize<SourceBrowseRequest>(command.PayloadJson, JsonOptions) ?? throw new InvalidOperationException("Browse-Payload fehlt.");
                    var source = new BackupObject { Kind = request.Kind, Direction = ObjectDirection.Source, Location = request.Location };
                    (string Username, string Password)? credential = string.IsNullOrWhiteSpace(request.SmbUsername) || request.SmbPassword is null ? null : (request.SmbUsername!, request.SmbPassword!);
                    var result = await sourceBrowser.BrowseAsync(source, request.Path, credential, cancellationToken);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(result, JsonOptions), "", cancellationToken);
                    break;
                }
                case SecondaryCommandKind.CreateProxmoxNativeBackup:
                {
                    var request = JsonSerializer.Deserialize<ProxmoxNativeBackupRequest>(command.PayloadJson, JsonOptions)
                        ?? throw new InvalidOperationException("Proxmox-Native-Payload fehlt.");
                    var result = await proxmoxNative.ExecuteAsync(
                        request,
                        heartbeatCancellation => ProgressAsync(client, command.Id, 0, 0, 0, heartbeatCancellation),
                        cancellationToken);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(result, JsonOptions), "", cancellationToken);
                    break;
                }
                case SecondaryCommandKind.ExportSource:
                {
                    var payload = JsonSerializer.Deserialize<SecondaryExportPayload>(command.PayloadJson) ?? throw new InvalidOperationException("Export-Payload fehlt.");
                    ArchiveProgress latest = new(0, 0, 0, 0, 0);
                    var preparation = transfers.PrepareSourceArchiveAsync(
                        payload.Source,
                        payload.Consistency,
                        cancellationToken,
                        progress => latest = progress,
                        enableBackpressure: true);
                    await PushGrowingSourceAsync(client, command, preparation, payload.JobId, () => latest, cancellationToken);
                    var metrics = transfers.GetSourceMetrics(command.TransferId);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(metrics), "", cancellationToken);
                    transfers.CleanupSourceArtifacts(command.TransferId);
                    break;
                }
                case SecondaryCommandKind.ImportTarget:
                {
                    var payload = JsonSerializer.Deserialize<SecondaryImportPayload>(command.PayloadJson) ?? throw new InvalidOperationException("Import-Payload fehlt.");
                    var destination = await PullTargetAsync(client, command, payload, cancellationToken);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(destination), "", cancellationToken);
                    break;
                }
                case SecondaryCommandKind.ImportStreamingTarget:
                {
                    var payload = JsonSerializer.Deserialize<SecondaryStreamingImportPayload>(command.PayloadJson)
                        ?? throw new InvalidOperationException("Streaming-Import-Payload fehlt.");
                    var destination = await PullStreamingTargetAsync(client, command, payload, cancellationToken);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(destination), "", cancellationToken);
                    break;
                }
                case SecondaryCommandKind.StreamSourceToTarget:
                {
                    var payload = JsonSerializer.Deserialize<SecondaryLocalStreamingPayload>(command.PayloadJson)
                        ?? throw new InvalidOperationException("Lokales Streaming-Payload fehlt.");
                    var result = await StreamLocalSourceToTargetAsync(client, command, payload, cancellationToken);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(result), "", cancellationToken);
                    transfers.CleanupSourceArtifacts(command.TransferId);
                    break;
                }
                case SecondaryCommandKind.ExportArchive:
                {
                    var payload = JsonSerializer.Deserialize<SecondaryArchiveExportPayload>(command.PayloadJson) ?? throw new InvalidOperationException("Restore-Export-Payload fehlt.");
                    var prepared = await restoreArchives.PrepareSecondaryExportAsync(payload, command.TransferId, cancellationToken);
                    try
                    {
                        var length = new FileInfo(prepared.ArchivePath).Length;
                        await PushSourceAsync(client, command, prepared.ArchivePath, 0, length, cancellationToken);
                        await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(new { Bytes = length }), "", cancellationToken);
                    }
                    finally
                    {
                        if (prepared.DeleteAfter)
                        {
                            try { File.Delete(prepared.ArchivePath); } catch { }
                        }
                    }
                    break;
                }
                case SecondaryCommandKind.ApplyRestore:
                {
                    var payload = JsonSerializer.Deserialize<SecondaryRestorePayload>(command.PayloadJson) ?? throw new InvalidOperationException("Restore-Payload fehlt.");
                    var archive = await PullArchiveAsync(client, command, payload.Target.TaskId, payload.TotalBytes, payload.Sha256, cancellationToken);
                    try
                    {
                        await archiveService.ApplyRestoreArchiveAsync(payload.Target.Kind, payload.Target.Location, archive, cancellationToken);
                        var destination = FormatRestoreDestination(payload.Target, payload.RestoreFolderName);
                        await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(destination), "", cancellationToken);
                    }
                    finally
                    {
                        try { File.Delete(archive); } catch { }
                    }
                    break;
                }
                case SecondaryCommandKind.PrepareIncrementalSource:
                {
                    var payload = JsonSerializer.Deserialize<IncrementalSourceCommandPayload>(command.PayloadJson, JsonOptions)
                        ?? throw new InvalidOperationException("Incremental-Source-Payload fehlt.");
                    var source = new BackupObject
                    {
                        Kind = payload.Source.Kind,
                        Direction = ObjectDirection.Source,
                        Location = payload.Source.Location
                    };
                    (string Username, string Password)? credential = string.IsNullOrWhiteSpace(payload.Source.SmbUsername) || payload.Source.SmbPassword is null
                        ? null
                        : (payload.Source.SmbUsername!, payload.Source.SmbPassword!);
                    var preparation = await incrementalSources.PrepareAsync(
                        source,
                        credential,
                        payload.TaskToken,
                        command.TransferId,
                        payload.ChunkSizeMiB,
                        cancellationToken,
                        payload.Source.IncludedPaths);
                    preparation.Manifest.Method = payload.Method;
                    var accepted = await UploadIncrementalManifestAsync(client, command.TransferId, preparation.Manifest, cancellationToken);
                    await PushIncrementalChunksAsync(client, command, preparation, accepted, cancellationToken);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(accepted, JsonOptions), "", cancellationToken);
                    TryDeleteDirectory(preparation.WorkingDirectory);
                    break;
                }
                case SecondaryCommandKind.ApplyIncrementalTarget:
                {
                    var payload = JsonSerializer.Deserialize<IncrementalTargetCommandPayload>(command.PayloadJson, JsonOptions)
                        ?? throw new InvalidOperationException("Incremental-Target-Payload fehlt.");
                    var manifest = await PullIncrementalManifestAndChunksAsync(client, command, cancellationToken);
                    var previous = await incrementalRepository.LoadPreviousManifestAsync(payload.TaskToken, cancellationToken);
                    var task = new BackupTask
                    {
                        Id = payload.TaskId,
                        Token = payload.TaskToken,
                        Method = manifest.Method,
                        ChunkSizeMiB = Math.Max(1, manifest.ChunkSizeBytes / 1024 / 1024)
                    };
                    var target = new BackupObject
                    {
                        Id = payload.Target.TaskId,
                        Kind = payload.Target.Kind,
                        Direction = ObjectDirection.Target,
                        Location = payload.Target.Location
                    };
                    var job = new TransferJob
                    {
                        Id = payload.JobId,
                        TaskId = payload.TaskId,
                        TaskToken = payload.TaskToken,
                        TransferId = command.TransferId,
                        Method = manifest.Method
                    };
                    (string Username, string Password)? credential = string.IsNullOrWhiteSpace(payload.Target.SmbUsername) || payload.Target.SmbPassword is null
                        ? null
                        : (payload.Target.SmbUsername!, payload.Target.SmbPassword!);
                    var result = await incrementalRepository.ApplyWithoutPrimaryCatalogAsync(task, target, job, manifest, previous, credential, cancellationToken);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(result, JsonOptions), "", cancellationToken);
                    TryDeleteDirectory(incrementalSources.TransferDirectory(command.TransferId));
                    break;
                }
                case SecondaryCommandKind.ExportIncrementalSnapshot:
                {
                    var payload = JsonSerializer.Deserialize<IncrementalSnapshotExportPayload>(command.PayloadJson, JsonOptions)
                        ?? throw new InvalidOperationException("Incremental-Snapshot-Export-Payload fehlt.");
                    var task = new BackupTask
                    {
                        Id = payload.TaskId,
                        Token = payload.TaskToken,
                        Method = BackupMethod.ReverseIncremental
                    };
                    var target = new BackupObject
                    {
                        Kind = payload.Target.Kind,
                        Direction = ObjectDirection.Target,
                        Location = payload.Target.Location
                    };
                    (string Username, string Password)? credential = string.IsNullOrWhiteSpace(payload.Target.SmbUsername) || payload.Target.SmbPassword is null
                        ? null
                        : (payload.Target.SmbUsername!, payload.Target.SmbPassword!);
                    var outputPath = Path.Combine(archiveService.CacheDirectory, $"incremental-export-{command.TransferId}.tar");
                    try
                    {
                        await incrementalRepository.CreateSnapshotArchiveAsync(task, target, payload.SnapshotToken, credential, outputPath, cancellationToken);
                        var length = new FileInfo(outputPath).Length;
                        await PushSourceAsync(client, command, outputPath, 0, length, cancellationToken);
                        await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(new { Bytes = length }), "", cancellationToken);
                    }
                    finally { try { File.Delete(outputPath); } catch { } }
                    break;
                }
                case SecondaryCommandKind.ApplyRetention:
                {
                    var payload = JsonSerializer.Deserialize<RetentionCleanupPayload>(command.PayloadJson, JsonOptions)
                        ?? throw new InvalidOperationException("Retention-Payload fehlt.");
                    var task = new BackupTask
                    {
                        Id = payload.TaskId,
                        Token = payload.TaskToken,
                        Retention = payload.Retention
                    };
                    var target = new BackupObject
                    {
                        Kind = payload.Target.Kind,
                        Direction = ObjectDirection.Target,
                        Location = payload.Target.Location
                    };
                    (string Username, string Password)? credential = string.IsNullOrWhiteSpace(payload.Target.SmbUsername) || payload.Target.SmbPassword is null
                        ? null
                        : (payload.Target.SmbUsername!, payload.Target.SmbPassword!);
                    var result = await retentionService.ApplyPhysicalAsync(
                        task,
                        target,
                        payload.ExpiredVersions,
                        payload.RetainedSnapshotTokens,
                        credential,
                        cancellationToken);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(result, JsonOptions), "", cancellationToken);
                    break;
                }
                default: throw new InvalidOperationException($"Unbekannter Secondary-Befehl: {command.Kind}");
            }
        }
        catch (OperationCanceledException) when (cmdCts.IsCancellationRequested && !appCancellation.IsCancellationRequested)
        {
            // User-initiated cancel: report it (Complete maps it to "Cancelled" via the command flag) and
            // reclaim the secondary's source cache. The command handlers' own finally blocks release any lease.
            logger.LogInformation("Secondary command {CommandId} was cancelled by the user", command.Id);
            if (command.Kind is SecondaryCommandKind.ExportSource or SecondaryCommandKind.StreamSourceToTarget
                && Guid.TryParse(command.TransferId, out _))
            {
                try { transfers.CleanupSourceArtifacts(command.TransferId); } catch (Exception cleanupEx) { logger.LogWarning(cleanupEx, "Cancel cleanup failed for {CommandId}", command.Id); }
            }
            await CompleteAsync(client, command.Id, false, "", "Vom Benutzer abgebrochen", CancellationToken.None);
        }
        catch (OperationCanceledException) when (appCancellation.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Secondary command {CommandId} failed", command.Id);
            await CompleteAsync(client, command.Id, false, "", ex.Message, CancellationToken.None);
        }
        finally
        {
            _commandCts.TryRemove(command.Id, out _);
        }
    }

    private async Task<SecondaryLocalStreamingResult> StreamLocalSourceToTargetAsync(
        HttpClient client,
        SecondaryCommandEnvelope command,
        SecondaryLocalStreamingPayload payload,
        CancellationToken cancellationToken)
    {
        ArchiveProgress latest = new(0, 0, 0, 0, 0);
        // Reset any stale target checkpoint from a previous failed attempt: with the sparse cache the source
        // archive is rebuilt from scratch on retry and may differ byte-for-byte, so resuming onto an old
        // target partial would splice mismatched content. Always restart the target from offset 0.
        await transfers.ResetStreamingTargetAsync(command.TransferId, payload.Target, cancellationToken);
        var preparation = transfers.PrepareSourceArchiveAsync(
            payload.Source,
            payload.Consistency,
            cancellationToken,
            progress => latest = progress,
            enableBackpressure: true);
        var started = Stopwatch.StartNew();
        var syncSpeed = new SpeedWindow();
        long synced = 0;
        long releasedUpTo = 0;
        long lastObservedAvailable = -1;
        long lastSourceBytes = -1;
        var lastForwardAt = started.Elapsed;
        var lastHeartbeat = started.Elapsed;
        GatewayStreamingWriteResult? write = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = transfers.SourceArchivePath(command.TransferId);
            var buildingPath = transfers.SourceBuildingPath(command.TransferId);
            var availablePath = File.Exists(finalPath) ? finalPath : buildingPath;
            var available = TryGetFileLength(availablePath);
            if (available > synced)
            {
                // A single tail copy can be up to the backpressure high watermark; pump heartbeats during it
                // so a slow target does not trip the idle watchdog mid-copy.
                try
                {
                    write = await RunWithHeartbeatAsync(
                        client, command.Id, synced, latest.EstimatedStoredBytes, latest.SourceBytes, JobPhase.Writing,
                        ct => transfers.SyncTargetCheckpointAsync(command.TransferId, payload.Target, availablePath, final: false, ct),
                        cancellationToken);
                }
                catch (FileNotFoundException) when (File.Exists(finalPath))
                {
                    continue;
                }
                synced = available;
                // Release the producer (backpressure) and reclaim the transferred cache region on disk.
                transfers.ReportConsumed(command.TransferId, synced);
                releasedUpTo = transfers.ReleaseConsumedSpace(availablePath, synced, releasedUpTo);
                var speed = syncSpeed.Sample(synced);
                var estimate = latest.EstimatedStoredBytes > 0 ? latest.EstimatedStoredBytes : synced;
                await ProgressAsync(client, command.Id, synced, estimate, speed, cancellationToken, latest.SourceBytes, write.WrittenBytes, latest.SpeedBytesPerSecond, speed, JobPhase.Writing, latest.EstimatedSourceBytes, latest.EstimatedStoredBytes);
                continue;
            }
            if (!preparation.IsCompleted)
            {
                // Liveness = the source is still being read/compressed (SourceBytes grows), the cache file
                // grows, or we are deliberately throttled. File length alone misses long compressible spans.
                if (available > lastObservedAvailable || latest.SourceBytes > lastSourceBytes || transfers.IsThrottled(command.TransferId)) lastForwardAt = started.Elapsed;
                lastObservedAvailable = Math.Max(lastObservedAvailable, available);
                lastSourceBytes = Math.Max(lastSourceBytes, latest.SourceBytes);
                if (started.Elapsed - lastHeartbeat >= HeartbeatInterval && started.Elapsed - lastForwardAt < MaxSilentBuildWindow)
                {
                    var phase = transfers.IsThrottled(command.TransferId) ? JobPhase.ReadPausedSlowTransfer : JobPhase.Reading;
                    await ProgressAsync(client, command.Id, synced, latest.EstimatedStoredBytes, 0, cancellationToken, latest.SourceBytes, write?.WrittenBytes ?? 0, latest.SpeedBytesPerSecond, 0, phase, latest.EstimatedSourceBytes, latest.EstimatedStoredBytes);
                    lastHeartbeat = started.Elapsed;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            var archive = await preparation;
            var metrics = transfers.GetSourceMetrics(command.TransferId);
            write = await RunWithHeartbeatAsync(
                client, command.Id, metrics.StoredBytes, metrics.StoredBytes, metrics.SourceBytes, JobPhase.Finalizing,
                ct => transfers.SyncTargetCheckpointAsync(command.TransferId, payload.Target, archive, final: true, ct),
                cancellationToken);
            await ProgressAsync(client, command.Id, metrics.StoredBytes, metrics.StoredBytes, 0, cancellationToken, metrics.SourceBytes, write.WrittenBytes, 0, 0, JobPhase.Finalizing, metrics.SourceBytes, metrics.StoredBytes);
            return new SecondaryLocalStreamingResult(metrics, write.Destination);
        }
    }

    private async Task PushGrowingSourceAsync(
        HttpClient client,
        SecondaryCommandEnvelope command,
        Task<string> preparation,
        long jobId,
        Func<ArchiveProgress> progress,
        CancellationToken cancellationToken)
    {
        var offset = await GetOffsetAsync(client, $"/api/secondary/transfers/{command.TransferId}/source-status", cancellationToken);
        var started = Stopwatch.StartNew();
        var uploadSpeed = new SpeedWindow();
        long releasedUpTo = 0;
        long lastObservedAvailable = -1;
        long lastSourceBytes = -1;
        var lastForwardAt = started.Elapsed;
        var lastHeartbeat = started.Elapsed;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = transfers.SourceArchivePath(command.TransferId);
            var buildingPath = transfers.SourceBuildingPath(command.TransferId);
            var availablePath = File.Exists(finalPath) ? finalPath : buildingPath;
            var available = TryGetFileLength(availablePath);

            if (offset < available)
            {
                var count = (int)Math.Min(ChunkSize, available - offset);
                var buffer = new byte[count];
                try
                {
                    await using var input = new FileStream(availablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    input.Position = offset;
                    await input.ReadExactlyAsync(buffer, cancellationToken);
                }
                catch (FileNotFoundException) when (File.Exists(finalPath))
                {
                    continue;
                }

                var result = await SendSourceChunkAsync(client, command, jobId, offset, -1, "", false, buffer, cancellationToken);
                if (!result.Success && result.Offset == offset) throw new IOException(result.Message);
                offset = result.Offset;
                transfers.ReportConsumed(command.TransferId, offset);
                releasedUpTo = transfers.ReleaseConsumedSpace(availablePath, offset, releasedUpTo);
                var current = progress();
                var speed = uploadSpeed.Sample(offset);
                var estimatedTotal = current.EstimatedStoredBytes > 0 ? current.EstimatedStoredBytes : Math.Max(offset, current.StoredBytes);
                var phase = transfers.IsThrottled(command.TransferId) ? JobPhase.ReadPausedSlowTransfer : JobPhase.Transferring;
                await ProgressAsync(
                    client,
                    command.Id,
                    offset,
                    estimatedTotal,
                    speed,
                    cancellationToken,
                    current.SourceBytes,
                    0,
                    current.SpeedBytesPerSecond,
                    0,
                    phase,
                    current.EstimatedSourceBytes,
                    current.EstimatedStoredBytes);
                continue;
            }

            if (!preparation.IsCompleted)
            {
                var current = progress();
                if (available > lastObservedAvailable || current.SourceBytes > lastSourceBytes || transfers.IsThrottled(command.TransferId)) lastForwardAt = started.Elapsed;
                lastObservedAvailable = Math.Max(lastObservedAvailable, available);
                lastSourceBytes = Math.Max(lastSourceBytes, current.SourceBytes);
                if (started.Elapsed - lastHeartbeat >= HeartbeatInterval && started.Elapsed - lastForwardAt < MaxSilentBuildWindow)
                {
                    var phase = transfers.IsThrottled(command.TransferId) ? JobPhase.ReadPausedSlowTransfer : JobPhase.Reading;
                    await ProgressAsync(client, command.Id, offset, current.EstimatedStoredBytes, 0, cancellationToken, current.SourceBytes, 0, current.SpeedBytesPerSecond, 0, phase, current.EstimatedSourceBytes, current.EstimatedStoredBytes);
                    lastHeartbeat = started.Elapsed;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            var archivePath = await preparation;
            var total = new FileInfo(archivePath).Length;
            if (offset < total) continue;
            var metrics = transfers.GetSourceMetrics(command.TransferId);
            var finalResult = await RunWithHeartbeatAsync(
                client, command.Id, offset, metrics.StoredBytes, metrics.SourceBytes, JobPhase.Integrity,
                ct => SendSourceChunkAsync(client, command, jobId, offset, total, metrics.Sha256, true, [], ct),
                cancellationToken);
            if (!finalResult.Success || !finalResult.Completed) throw new IOException(finalResult.Message);
            var finalProgress = progress();
            await ProgressAsync(
                client,
                command.Id,
                total,
                total,
                (long)(total / Math.Max(0.001, started.Elapsed.TotalSeconds)),
                cancellationToken,
                finalProgress.SourceBytes > 0 ? finalProgress.SourceBytes : metrics.SourceBytes,
                total,
                0,
                0,
                JobPhase.Finalizing,
                metrics.SourceBytes,
                metrics.StoredBytes);
            return;
        }
    }

    private async Task<GatewayUploadResult> SendSourceChunkAsync(
        HttpClient client,
        SecondaryCommandEnvelope command,
        long jobId,
        long offset,
        long total,
        string sha256,
        bool final,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, _primaryEndpoint + $"/api/secondary/transfers/{command.TransferId}/source");
        AddToken(request);
        request.Headers.Add("X-MatBu-Transfer-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-MatBu-Transfer-Final", final.ToString());
        request.Headers.Add("X-MatBu-Transfer-Job-Id", jobId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-MatBu-Transfer-Total", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-MatBu-Transfer-Sha256", sha256);
        request.Content = new ByteArrayContent(buffer);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayUploadResult>(cancellationToken: cancellationToken)
            ?? throw new IOException("Primary antwortete ohne Source-Status.");
    }

    private async Task PushSourceAsync(HttpClient client, SecondaryCommandEnvelope command, string archivePath, long jobId, long total, CancellationToken cancellationToken)
    {
        var sha256 = await ArchiveIntegrity.ComputeSha256Async(archivePath, cancellationToken);
        var offset = await GetOffsetAsync(client, $"/api/secondary/transfers/{command.TransferId}/source-status?sha256={sha256}", cancellationToken);
        var started = Stopwatch.StartNew();
        while (offset < total)
        {
            var count = (int)Math.Min(ChunkSize, total - offset);
            var buffer = new byte[count];
            await using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read)) { input.Position = offset; await input.ReadExactlyAsync(buffer, cancellationToken); }
            using var request = new HttpRequestMessage(HttpMethod.Put, _primaryEndpoint + $"/api/secondary/transfers/{command.TransferId}/source");
            AddToken(request);
            request.Headers.Add("X-MatBu-Transfer-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-MatBu-Transfer-Final", (offset + count >= total).ToString());
            request.Headers.Add("X-MatBu-Transfer-Job-Id", jobId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-MatBu-Transfer-Total", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-MatBu-Transfer-Sha256", sha256);
            request.Content = new ByteArrayContent(buffer);
            using var response = await client.SendAsync(request, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<GatewayUploadResult>(cancellationToken: cancellationToken);
            if (result is null) throw new IOException("Primary antwortete ohne Source-Status.");
            if (!result.Success && result.Offset == offset) throw new IOException(result.Message);
            offset = result.Offset;
            var speed = (long)(offset / Math.Max(0.001, started.Elapsed.TotalSeconds));
            await ProgressAsync(client, command.Id, offset, total, speed, cancellationToken);
        }
    }

    private async Task<IncrementalManifestUploadResult> UploadIncrementalManifestAsync(
        HttpClient client,
        string transferId,
        IncrementalBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _primaryEndpoint + $"/api/secondary/transfers/{transferId}/incremental-manifest");
        AddToken(request);
        request.Content = JsonContent.Create(manifest, options: JsonOptions);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IncrementalManifestUploadResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Die Primary lieferte keinen Incremental-Transferplan.");
    }

    private async Task PushIncrementalChunksAsync(
        HttpClient client,
        SecondaryCommandEnvelope command,
        IncrementalSourcePreparation preparation,
        IncrementalManifestUploadResult accepted,
        CancellationToken cancellationToken)
    {
        var hashes = accepted.MissingHashes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var total = hashes.Sum(hash => new FileInfo(incrementalSources.ChunkPath(command.TransferId, hash)).Length);
        long transferred = 0;
        var started = Stopwatch.StartNew();
        foreach (var hash in hashes)
        {
            var path = incrementalSources.ChunkPath(command.TransferId, hash);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var request = new HttpRequestMessage(HttpMethod.Put, _primaryEndpoint + $"/api/secondary/transfers/{command.TransferId}/incremental-chunks/{hash}")
            {
                Content = new StreamContent(input)
            };
            request.Content.Headers.ContentLength = input.Length;
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            AddToken(request);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            transferred += input.Length;
            var speed = (long)(transferred / Math.Max(0.001, started.Elapsed.TotalSeconds));
            await ProgressAsync(client, command.Id, transferred, total, speed, cancellationToken);
        }
    }

    private async Task<IncrementalBackupManifest> PullIncrementalManifestAndChunksAsync(
        HttpClient client,
        SecondaryCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, _primaryEndpoint + $"/api/secondary/transfers/{command.TransferId}/incremental-manifest");
        AddToken(manifestRequest);
        using var manifestResponse = await client.SendAsync(manifestRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();
        var manifest = await manifestResponse.Content.ReadFromJsonAsync<IncrementalBackupManifest>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Das Incremental-Manifest der Primary ist leer.");
        var missing = incrementalSources.FindMissingChangedHashes(manifest, command.TransferId);
        var expectedByHash = manifest.Files.SelectMany(file => file.Chunks).Where(chunk => chunk.Changed)
            .GroupBy(chunk => chunk.Hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Length, StringComparer.OrdinalIgnoreCase);
        var total = missing.Sum(hash => (long)expectedByHash[hash]);
        long transferred = 0;
        var started = Stopwatch.StartNew();
        foreach (var hash in missing)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _primaryEndpoint + $"/api/secondary/transfers/{command.TransferId}/incremental-chunks/{hash}");
            AddToken(request);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var length = await incrementalSources.ReceiveChunkAsync(command.TransferId, hash, input, cancellationToken);
            transferred += length;
            var speed = (long)(transferred / Math.Max(0.001, started.Elapsed.TotalSeconds));
            await ProgressAsync(client, command.Id, transferred, total, speed, cancellationToken);
        }
        await incrementalSources.VerifyChangedChunksAsync(manifest, command.TransferId, cancellationToken);
        return manifest;
    }

    private async Task<string> PullStreamingTargetAsync(
        HttpClient client,
        SecondaryCommandEnvelope command,
        SecondaryStreamingImportPayload payload,
        CancellationToken cancellationToken)
    {
        var dataPath = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? "/data";
        var partial = Path.Combine(dataPath, "transfer-cache", $"stream-target-{command.TransferId}.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        var started = Stopwatch.StartNew();
        var lastHeartbeat = TimeSpan.Zero;
        GatewayStreamingWriteResult? write = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await GetStreamStatusAsync(client, command.TransferId, cancellationToken);
            if (status.Failed) throw new IOException(string.IsNullOrWhiteSpace(status.Error) ? "Die Streaming-Quelle ist fehlgeschlagen." : status.Error);
            if (offset > status.AvailableBytes)
            {
                File.Delete(partial);
                offset = 0;
            }

            if (offset < status.AvailableBytes)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _primaryEndpoint + $"/api/secondary/transfers/{command.TransferId}/stream?offset={offset}&maxBytes={ChunkSize}");
                AddToken(request);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, ChunkSize, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                offset = new FileInfo(partial).Length;
                write = await transfers.SyncTargetCheckpointAsync(command.TransferId, payload.Target, partial, final: false, cancellationToken);
                var speed = (long)(offset / Math.Max(0.001, started.Elapsed.TotalSeconds));
                await ProgressAsync(client, command.Id, offset, Math.Max(status.TotalBytes, status.AvailableBytes), speed, cancellationToken, 0, write.WrittenBytes, 0, speed);
                lastHeartbeat = started.Elapsed;
                continue;
            }

            if (status.Completed)
            {
                if (!ArchiveIntegrity.IsSha256(status.Sha256)) throw new InvalidDataException("Die Primary meldet keinen gueltigen SHA-256-Hash fuer den Streaming-Transfer.");
                await ArchiveIntegrity.VerifySha256Async(partial, status.Sha256, cancellationToken);
                write = await transfers.SyncTargetCheckpointAsync(command.TransferId, payload.Target, partial, final: true, cancellationToken);
                var speed = (long)(offset / Math.Max(0.001, started.Elapsed.TotalSeconds));
                await ProgressAsync(client, command.Id, offset, status.TotalBytes, speed, cancellationToken, 0, write.WrittenBytes, 0, 0);
                try { File.Delete(partial); } catch { }
                return write.Destination;
            }

            if (started.Elapsed - lastHeartbeat >= TimeSpan.FromSeconds(5))
            {
                await ProgressAsync(client, command.Id, offset, Math.Max(status.TotalBytes, status.AvailableBytes), 0, cancellationToken, 0, write?.WrittenBytes ?? 0, 0, 0);
                lastHeartbeat = started.Elapsed;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    private async Task<GatewayStreamStatus> GetStreamStatusAsync(HttpClient client, string transferId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _primaryEndpoint + $"/api/secondary/transfers/{transferId}/stream-status");
        AddToken(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayStreamStatus>(JsonOptions, cancellationToken)
            ?? throw new IOException("Primary antwortete ohne Streaming-Status.");
    }

    private async Task<string> PullTargetAsync(HttpClient client, SecondaryCommandEnvelope command, SecondaryImportPayload payload, CancellationToken cancellationToken)
    {
        var final = await PullArchiveAsync(client, command, payload.Target.TaskId, payload.TotalBytes, payload.Sha256, cancellationToken);
        try { return await transfers.ApplyTargetArchiveAsync(final, command.TransferId, payload.Target, cancellationToken); }
        finally { try { File.Delete(final); } catch { } }
    }

    private async Task<string> PullArchiveAsync(HttpClient client, SecondaryCommandEnvelope command, long taskId, long expectedTotalBytes, string expectedSha256, CancellationToken cancellationToken)
    {
        var dataPath = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? "/data";
        var partial = Path.Combine(dataPath, "transfer-cache", $"gateway-upload-{command.TransferId}.tar.partial");
        var final = partial[..^".partial".Length];
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        if (!ArchiveIntegrity.IsSha256(expectedSha256)) throw new InvalidDataException("Der Transfer enthält keine gültige SHA-256-Prüfsumme.");
        if (File.Exists(final) && (expectedTotalBytes <= 0 || new FileInfo(final).Length == expectedTotalBytes))
        {
            try
            {
                await ArchiveIntegrity.VerifySha256Async(final, expectedSha256, cancellationToken);
                return final;
            }
            catch (InvalidDataException) { File.Delete(final); }
        }
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (expectedTotalBytes > 0 && offset > expectedTotalBytes) { File.Delete(partial); offset = 0; }
        var started = Stopwatch.StartNew();
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _primaryEndpoint + $"/api/secondary/transfers/{command.TransferId}/target?taskId={taskId}&offset={offset}");
            AddToken(request);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength is long length ? length + offset : expectedTotalBytes);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                offset += read;
                var speed = (long)(offset / Math.Max(0.001, started.Elapsed.TotalSeconds));
                await ProgressAsync(client, command.Id, offset, total, speed, cancellationToken);
            }
            if (total <= 0 || offset >= total) break;
        }

        File.Move(partial, final, overwrite: true);
        await ArchiveIntegrity.VerifySha256Async(final, expectedSha256, cancellationToken);
        return final;
    }

    private static string FormatRestoreDestination(GatewayTargetRequest target, string restoreFolderName) => target.Kind == ObjectKind.DockerVolume
        ? $"{target.Location}:/{restoreFolderName}"
        : Path.Combine(target.Location, restoreFolderName);

    private async Task<long> GetOffsetAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _primaryEndpoint + path);
        AddToken(request);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<TransferOffsetResponse>(cancellationToken: cancellationToken);
        return status?.Offset ?? 0;
    }

    private async Task ProgressAsync(
        HttpClient client,
        long commandId,
        long bytes,
        long total,
        long speed,
        CancellationToken cancellationToken,
        long bytesRead = 0,
        long bytesWritten = 0,
        long readSpeed = 0,
        long writeSpeed = 0,
        string? phase = null,
        long estimatedSource = 0,
        long estimatedStored = 0)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _primaryEndpoint + $"/api/secondary/commands/{commandId}/progress");
        AddToken(request);
        request.Content = JsonContent.Create(new SecondaryCommandProgress(bytes, total, speed, "Secondary-Verbindung", bytesRead, bytesWritten, readSpeed, writeSpeed, phase, estimatedSource, estimatedStored));
        using var response = await client.SendAsync(request, cancellationToken);
        // A 409 on the heartbeat is the primary's stop directive for a user-cancelled command: abort this command.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            if (_commandCts.TryGetValue(commandId, out var cts)) cts.Cancel();
            throw new OperationCanceledException($"Secondary-Kommando {commandId} wurde abgebrochen.");
        }
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Run a single long await (e.g. the final chunk PUT, during which the primary hashes the whole archive)
    /// while a background loop keeps posting heartbeats, so the primary's idle watchdog does not kill the
    /// command at the very end. The pump is inherently live because the wrapped operation is running.
    /// </summary>
    private async Task<T> RunWithHeartbeatAsync<T>(
        HttpClient client,
        long commandId,
        long bytes,
        long total,
        long sourceBytes,
        string phase,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = Task.Run(async () =>
        {
            try
            {
                while (!pumpCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatInterval, pumpCts.Token);
                    if (pumpCts.Token.IsCancellationRequested) break;
                    try { await ProgressAsync(client, commandId, bytes, total, 0, pumpCts.Token, sourceBytes, 0, 0, 0, phase, sourceBytes, total); }
                    catch { /* heartbeat is best-effort */ }
                }
            }
            catch (OperationCanceledException) { }
        }, pumpCts.Token);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            pumpCts.Cancel();
            try { await pump; } catch { /* ignore */ }
        }
    }

    private async Task CompleteAsync(HttpClient client, long commandId, bool success, string result, string error, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _primaryEndpoint + $"/api/secondary/commands/{commandId}/complete");
        AddToken(request);
        request.Content = JsonContent.Create(new SecondaryCommandCompletion(success, result, error));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private void AddToken(HttpRequestMessage request) => request.Headers.Add("X-MatBu-Instance-Token", _token);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Transfer-Caches are opportunistic. A later maintenance pass may remove leftovers.
        }
    }

    private static long TryGetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (FileNotFoundException) { return 0; }
    }

    private sealed record TransferOffsetResponse(long Offset);
}
