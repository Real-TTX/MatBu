using System.Collections.Concurrent;
using System.Text.Json;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed record GatewaySourceRequest(string TransferId, ObjectKind Kind, string Location, string? SmbUsername, string? SmbPassword, long Offset = 0, BackupCompression Compression = BackupCompression.None, IReadOnlyList<string>? IncludedPaths = null);
public sealed record GatewayTargetRequest(long TaskId, ObjectKind Kind, string Location, string? SmbUsername, string? SmbPassword, BackupCompression Compression = BackupCompression.None, string Sha256 = "");
public sealed record GatewayUploadResult(bool Success, long Offset, bool Completed, string Message);
public sealed record GatewayArchiveMetrics(long SourceBytes, long StoredBytes, string Sha256 = "");
public sealed record GatewayStreamStatus(long AvailableBytes, bool Completed, long TotalBytes, string Sha256, bool Failed = false, string Error = "");
public sealed record GatewayStreamingWriteResult(string Destination, long WrittenBytes);

public sealed class GatewayTransferService(
    ArchiveService archiveService,
    DockerConsistencyService dockerConsistency,
    SmbClientService smbClient,
    PersistentStore store,
    ILogger<GatewayTransferService> logger)
{
    private const long MiB = 1024L * 1024L;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _transferLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PipelineRateState> _pipelineRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TransferBackpressureGate> _backpressure = new(StringComparer.OrdinalIgnoreCase);

    private static readonly long BacklogHighWatermark = ResolveWatermark("MATBU_TRANSFER_BACKLOG_HIGH_MIB", 512) * MiB;
    private static readonly long BacklogLowWatermark = ResolveWatermark("MATBU_TRANSFER_BACKLOG_LOW_MIB", 128) * MiB;
    private static readonly bool SparseCacheEnabled = Environment.GetEnvironmentVariable("MATBU_TRANSFER_SPARSE_CACHE") != "0";
    private const long SparseReleaseThreshold = 32L * MiB;

    private static long ResolveWatermark(string variable, long defaultMiB)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        return long.TryParse(configured, out var value) && value > 0 ? value : defaultMiB;
    }

    /// <summary>
    /// Deallocate the physical disk blocks of the source cache region the consumer has already uploaded, so
    /// the secondary keeps only the un-transferred backlog on disk. Punches in aligned batches; returns the
    /// new high-water mark of released bytes to pass back on the next call. No-op when sparse cache is off.
    /// </summary>
    public long ReleaseConsumedSpace(string path, long consumedOffset, long alreadyReleased)
    {
        if (!SparseCacheEnabled || consumedOffset - alreadyReleased < SparseReleaseThreshold) return alreadyReleased;
        var (offset, length) = SparseFile.AlignedRange(alreadyReleased, consumedOffset);
        if (length <= 0) return alreadyReleased;
        if (!SparseFile.TryPunchHole(path, offset, length)) return alreadyReleased;
        logger.LogDebug("Released {Bytes} bytes of transferred source cache up to offset {Offset}", length, offset + length);
        return offset + length;
    }

    private TransferBackpressureGate Gate(string transferId) =>
        _backpressure.GetOrAdd(transferId, _ => new TransferBackpressureGate(BacklogHighWatermark, BacklogLowWatermark));

    /// <summary>
    /// Report how many bytes the transfer consumer (upload/target sync) has drained from the source
    /// cache file. Used to throttle the producing archive build so the cache cannot outgrow the transfer.
    /// </summary>
    public void ReportConsumed(string transferId, long consumedOffset) => Gate(transferId).ReportConsumed(consumedOffset);

    /// <summary>True while the producer is currently held back because the transfer cannot keep up.</summary>
    public bool IsThrottled(string transferId) => _backpressure.TryGetValue(transferId, out var gate) && gate.IsPaused;

    private void WaitForCapacity(string transferId, long producedBytes, CancellationToken cancellationToken) =>
        Gate(transferId).WaitForCapacity(producedBytes, cancellationToken);

    public Task<string> PrepareSourceArchiveAsync(GatewaySourceRequest request, CancellationToken cancellationToken) =>
        PrepareSourceArchiveAsync(request, new BackupConsistencySettings(BackupConsistencyMode.None, "", "", "", 60), cancellationToken, null);

    public Task<string> PrepareSourceArchiveAsync(GatewaySourceRequest request, BackupConsistencySettings consistency, CancellationToken cancellationToken) =>
        PrepareSourceArchiveAsync(request, consistency, cancellationToken, null);

    public async Task<string> PrepareSourceArchiveAsync(
        GatewaySourceRequest request,
        BackupConsistencySettings consistency,
        CancellationToken cancellationToken,
        Action<ArchiveProgress>? progress,
        bool enableBackpressure = false)
    {
        EnsureTransferId(request.TransferId);
        var gate = _transferLocks.GetOrAdd(request.TransferId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var archivePath = SourceArchivePath(request.TransferId);
            if (File.Exists(archivePath))
            {
                // A previously streamed archive may have had consumed regions punched out, so it is no longer
                // safe to reuse as-is; rebuild it. Without sparse cache the file is intact and can be reused.
                if (!SparseCacheEnabled) return archivePath;
                File.Delete(archivePath);
            }

            var buildingPath = SourceBuildingPath(request.TransferId);
            if (File.Exists(buildingPath)) File.Delete(buildingPath);
            var source = new BackupObject { Kind = request.Kind, Location = request.Location };
            (string Username, string Password)? credential = string.IsNullOrWhiteSpace(request.SmbUsername) || request.SmbPassword is null ? null : (request.SmbUsername!, request.SmbPassword!);
            DockerConsistencyLease? lease = null;
            try
            {
                if (consistency.Mode != BackupConsistencyMode.None)
                    lease = await dockerConsistency.BeginAsync(consistency, cancellationToken);
                // Backpressure only makes sense when a consumer drains the growing cache concurrently (the
                // streaming secondary paths). For await-then-stream callers there is no concurrent consumer,
                // so throttling would deadlock — those pass enableBackpressure: false.
                Action<long>? throttle = enableBackpressure
                    ? (produced => WaitForCapacity(request.TransferId, produced, cancellationToken))
                    : null;
                var result = await archiveService.CreateCompressedAsync(
                    source,
                    credential,
                    archivePath,
                    request.Compression,
                    progress,
                    cancellationToken,
                    request.IncludedPaths,
                    throttle);
                // SHA-256 is hashed incrementally during the build, not re-read afterwards: the consumer may
                // already have punched holes into transferred regions, which would corrupt a post-hoc read.
                await File.WriteAllTextAsync(MetricsPath(request.TransferId), JsonSerializer.Serialize(new GatewayArchiveMetrics(result.SourceBytes, result.StoredBytes, result.Sha256)), cancellationToken);
                return archivePath;
            }
            finally
            {
                if (lease is not null) await dockerConsistency.EndAsync(consistency, lease, CancellationToken.None);
            }
        }
        finally { gate.Release(); }
    }

    public string SourceArchivePath(string transferId)
    {
        EnsureTransferId(transferId);
        return Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.tar");
    }

    public string SourceBuildingPath(string transferId) => SourceArchivePath(transferId) + ".building";

    /// <summary>
    /// Discard all cache artifacts for a cancelled transfer: the primary-written streaming target checkpoint
    /// (named task-{TaskId}-{JobId}, resolved from the job) plus the gateway source archive/partial/metrics.
    /// </summary>
    public async Task CancelTransferAsync(long jobId, string transferId, CancellationToken cancellationToken)
    {
        EnsureTransferId(transferId);
        if (jobId > 0)
        {
            var target = ResolveStreamingPrimaryTarget(jobId);
            if (target is not null) await ResetStreamingTargetAsync(target, cancellationToken);
        }
        CleanupSourceArtifacts(transferId);
    }

    public long CleanupSourceArtifacts(string transferId)
    {
        EnsureTransferId(transferId);
        var archive = SourceArchivePath(transferId);
        var paths = new[]
        {
            archive,
            archive + ".building",
            archive + ".partial",
            MetricsPath(transferId)
        };
        long reclaimed = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                reclaimed += new FileInfo(path).Length;
                File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Transfer cache artifact {Path} could not be removed", path);
            }
        }
        _pipelineRates.TryRemove(transferId, out _);
        _backpressure.TryRemove(transferId, out _);
        if (reclaimed > 0)
            logger.LogInformation("Removed {Bytes} bytes of completed source cache for transfer {TransferId}", reclaimed, transferId);
        return reclaimed;
    }

    public GatewayStreamStatus GetIncomingSourceStatus(string transferId)
    {
        EnsureTransferId(transferId);
        var final = SourceArchivePath(transferId);
        var partial = final + ".partial";
        var path = File.Exists(final) ? final : partial;
        var available = File.Exists(path) ? new FileInfo(path).Length : 0;
        var completed = File.Exists(final);
        var job = store.Read().TransferJobs.FirstOrDefault(item => item.TransferId.Equals(transferId, StringComparison.OrdinalIgnoreCase));
        var total = completed ? available : Math.Max(0, job?.TotalBytes ?? 0);
        var failed = job?.State is "Fehler" or "Failed";
        return new GatewayStreamStatus(available, completed, total, completed ? job?.ArchiveSha256 ?? "" : "", failed, failed ? job?.Error ?? "Streaming source failed." : "");
    }

    public Stream OpenIncomingSourceRange(string transferId, long offset, long maxBytes)
    {
        var status = GetIncomingSourceStatus(transferId);
        if (offset < 0 || offset > status.AvailableBytes) throw new ArgumentOutOfRangeException(nameof(offset));
        var final = SourceArchivePath(transferId);
        var path = File.Exists(final) ? final : final + ".partial";
        var length = Math.Min(Math.Max(0, maxBytes), status.AvailableBytes - offset);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new RangeReadStream(file, offset, length);
    }

    public async Task<GatewayStreamingWriteResult> SyncTargetCheckpointAsync(
        string transferId,
        GatewayTargetRequest target,
        string sourcePath,
        bool final,
        CancellationToken cancellationToken)
    {
        EnsureTransferId(transferId);
        var fileName = $"task-{target.TaskId}-{transferId}{ArchiveExtension(target.Compression)}";
        if (target.Kind == ObjectKind.LocalFolder)
        {
            Directory.CreateDirectory(target.Location);
            var destination = Path.Combine(target.Location, fileName);
            var partial = destination + ".partial";
            await CopyAvailableTailAsync(sourcePath, partial, cancellationToken);
            var written = new FileInfo(partial).Length;
            if (final) File.Move(partial, destination, overwrite: true);
            return new GatewayStreamingWriteResult(destination, written);
        }
        if (target.Kind == ObjectKind.Smb)
        {
            (string Username, string Password)? credential = string.IsNullOrWhiteSpace(target.SmbUsername) || target.SmbPassword is null
                ? null
                : (target.SmbUsername!, target.SmbPassword!);
            var written = await smbClient.SyncPartialFileAsync(target.Location, sourcePath, fileName, credential, cancellationToken);
            if (final) await smbClient.FinalizePartialFileAsync(target.Location, fileName, credential, cancellationToken);
            return new GatewayStreamingWriteResult($"{target.Location.TrimEnd('\\', '/')}/{fileName}", written);
        }
        throw new InvalidOperationException($"Streaming target type {target.Kind} is not supported.");
    }

    public long GetUploadOffset(string transferId)
    {
        EnsureTransferId(transferId);
        var path = UploadPartialPath(transferId);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    /// <summary>
    /// Discard the streaming-primary target checkpoint for a job (LocalFolder/Smb), resolved from the job's
    /// target. Called proactively at the start of a streamed run so a stale partial — or a finalized-but-
    /// failed destination — from a prior attempt cannot be spliced onto or re-acknowledged by a rebuilt source.
    /// </summary>
    public async Task ResetStreamingTargetAsync(long jobId, CancellationToken cancellationToken)
    {
        if (jobId <= 0) return;
        var target = ResolveStreamingPrimaryTarget(jobId);
        if (target is not null) await ResetStreamingTargetAsync(target, cancellationToken);
    }

    /// <summary>
    /// Discard any partially-written streaming target checkpoint for this transfer, so a retry starts the
    /// target from offset 0. Required because the sparse cache rebuilds the source archive from scratch on
    /// retry — resuming onto a stale partial would splice mismatched bytes into a corrupt archive.
    /// </summary>
    public async Task ResetStreamingTargetAsync(string transferId, GatewayTargetRequest target, CancellationToken cancellationToken)
    {
        EnsureTransferId(transferId);
        var fileName = $"task-{target.TaskId}-{transferId}{ArchiveExtension(target.Compression)}";
        try
        {
            if (target.Kind == ObjectKind.LocalFolder)
            {
                var partial = Path.Combine(target.Location, fileName) + ".partial";
                if (File.Exists(partial)) File.Delete(partial);
                return;
            }
            if (target.Kind == ObjectKind.Smb)
            {
                (string Username, string Password)? credential = string.IsNullOrWhiteSpace(target.SmbUsername) || target.SmbPassword is null
                    ? null
                    : (target.SmbUsername!, target.SmbPassword!);
                await smbClient.DeleteUploadPartialAsync(target.Location, fileName, credential, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Streaming target checkpoint reset failed for transfer {TransferId}", transferId);
        }
    }

    public Stream OpenSourceRange(string archivePath, long offset)
    {
        var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new RangeReadStream(file, offset, file.Length - offset);
    }

    public async Task<long> GetSourceOffsetAsync(string transferId, string expectedSha256, CancellationToken cancellationToken)
    {
        EnsureTransferId(transferId);
        // LocalFolder streaming-primary keeps no cache file; the resume offset comes from the live receive
        // state, or from the finalized target. A restart (no in-memory hash chain) must restart from 0.
        var job = store.Read().TransferJobs.FirstOrDefault(j => j.TransferId.Equals(transferId, StringComparison.OrdinalIgnoreCase));
        if (job is not null && ResolveStreamingPrimaryTarget(job.Id) is { } directTarget)
        {
            if (directTarget.Object.Kind == ObjectKind.LocalFolder)
            {
                var destinationPartial = directTarget.Destination + ".partial";
                if (File.Exists(destinationPartial)) return new FileInfo(destinationPartial).Length;
                if (File.Exists(directTarget.Destination)) return new FileInfo(directTarget.Destination).Length;
                return 0;
            }
            if (directTarget.Object.Kind == ObjectKind.Smb && SmbClientService.IsStreamingEnabled)
            {
                // Durable remote size is the resume cursor for the SMB direct route.
                var partialSize = await smbClient.GetRemoteFileSizeAsync(directTarget.Object.Location, directTarget.FileName + ".partial", directTarget.Credential, cancellationToken);
                if (partialSize > 0) return partialSize;
                return await smbClient.GetRemoteFileSizeAsync(directTarget.Object.Location, directTarget.FileName, directTarget.Credential, cancellationToken);
            }
        }
        var partial = Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.tar.partial");
        var final = Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.tar");
        if (File.Exists(partial)) return new FileInfo(partial).Length;
        if (!File.Exists(final)) return 0;
        if (!ArchiveIntegrity.IsSha256(expectedSha256)) return new FileInfo(final).Length;
        try
        {
            await ArchiveIntegrity.VerifySha256Async(final, expectedSha256, cancellationToken);
            return new FileInfo(final).Length;
        }
        catch (InvalidDataException)
        {
            File.Delete(final);
            return 0;
        }
    }

    public GatewayArchiveMetrics GetSourceMetrics(string transferId)
    {
        EnsureTransferId(transferId);
        try
        {
            var metricsPath = MetricsPath(transferId);
            if (File.Exists(metricsPath))
                return JsonSerializer.Deserialize<GatewayArchiveMetrics>(File.ReadAllText(metricsPath)) ?? new(0, 0);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Source archive metrics could not be read for {TransferId}", transferId); }
        var archivePath = Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.tar");
        return new GatewayArchiveMetrics(0, File.Exists(archivePath) ? new FileInfo(archivePath).Length : 0);
    }

    public async Task<GatewayUploadResult> ReceiveSourceChunkAsync(string transferId, long offset, bool final, long jobId, long totalBytes, string expectedSha256, Stream body, CancellationToken cancellationToken)
    {
        EnsureTransferId(transferId);
        var gate = _transferLocks.GetOrAdd($"source-{transferId}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Resolve the streaming-primary target once per chunk (one store read) and reuse it below.
            var target = jobId > 0 ? ResolveStreamingPrimaryTarget(jobId) : null;
            // Direct-to-target streaming: write chunks straight to the primary target, keeping NO full copy
            // in the transfer cache. LocalFolder writes the file directly; SMB writes offset-addressed via
            // SMBLibrary (footprint ~0 locally). SMB can be forced back to the cache path with
            // MATBU_SMB_STREAMING=0.
            if (target?.Object.Kind == ObjectKind.LocalFolder)
                return await ReceiveSourceChunkToLocalTargetAsync(transferId, offset, final, jobId, totalBytes, expectedSha256, target, body, cancellationToken);
            if (target?.Object.Kind == ObjectKind.Smb && SmbClientService.IsStreamingEnabled)
                return await ReceiveSourceChunkToSmbTargetAsync(transferId, offset, final, jobId, totalBytes, expectedSha256, target, body, cancellationToken);

            var partial = Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.tar.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            var completedArchive = SourceArchivePath(transferId);
            if (!File.Exists(partial) && File.Exists(completedArchive))
            {
                var completedLength = new FileInfo(completedArchive).Length;
                if (completedLength != offset)
                    return new GatewayUploadResult(false, completedLength, false, "Der abgeschlossene Source-Checkpoint hat einen anderen Offset.");
                if (!final)
                    return new GatewayUploadResult(true, completedLength, false, "Source-Daten sind bereits vollstaendig empfangen.");
                if (completedLength != totalBytes || !ArchiveIntegrity.IsSha256(expectedSha256))
                    return new GatewayUploadResult(false, completedLength, false, "Finale Source-Metadaten sind ungueltig.");
                await ArchiveIntegrity.VerifySha256Async(completedArchive, expectedSha256, cancellationToken);
                var completedWritten = target is null ? 0 : await SyncStreamingTargetAsync(target, completedArchive, final: true, cancellationToken);
                UpdatePipelineJob(jobId, transferId, completedLength, completedWritten, totalBytes, completedArchive);
                if (jobId > 0)
                {
                    store.Update(data =>
                    {
                        var job = data.TransferJobs.FirstOrDefault(item => item.Id == jobId);
                        if (job is null) return;
                        job.ArchiveSha256 = expectedSha256.ToLowerInvariant();
                        if (target is not null) job.ResolvedDestination = target.Destination;
                        job.UpdateDate = DateTimeOffset.UtcNow;
                    });
                }
                return new GatewayUploadResult(true, completedLength, true, "Source-Transfer war bereits abgeschlossen und wurde bestaetigt.");
            }
            var current = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (current != offset) return new GatewayUploadResult(false, current, false, "Der Source-Checkpoint stimmt nicht mit der Primary überein.");
            // Controlled abort before the cache volume is filled to zero (this path has no other disk bound).
            archiveService.EnsureCacheFreeSpace(0);
            await using (var output = new FileStream(partial, FileMode.Append, FileAccess.Write, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await body.CopyToAsync(output, 4 * 1024 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            current = new FileInfo(partial).Length;
            var written = target is null
                ? 0
                : await SyncStreamingTargetAsync(target, partial, final: false, cancellationToken);
            UpdatePipelineJob(jobId, transferId, current, written, totalBytes, partial);
            if (!final) return new GatewayUploadResult(true, current, false, "Source-Chunk gespeichert.");
            if (current != totalBytes || !ArchiveIntegrity.IsSha256(expectedSha256))
            {
                File.Delete(partial);
                await ResetStreamingTargetAsync(target, cancellationToken);
                return new GatewayUploadResult(false, 0, false, "Source-Transfer verworfen: Größe oder SHA-256-Prüfsumme fehlt.");
            }
            try
            {
                await ArchiveIntegrity.VerifySha256Async(partial, expectedSha256, cancellationToken);
            }
            catch (InvalidDataException ex)
            {
                File.Delete(partial);
                await ResetStreamingTargetAsync(target, cancellationToken);
                return new GatewayUploadResult(false, 0, false, ex.Message);
            }
            var archive = SourceArchivePath(transferId);
            File.Move(partial, archive, overwrite: true);
            if (target is not null)
            {
                written = await SyncStreamingTargetAsync(target, archive, final: true, cancellationToken);
                UpdatePipelineJob(jobId, transferId, current, written, totalBytes, archive);
            }
            if (jobId > 0)
            {
                store.Update(data =>
                {
                    var job = data.TransferJobs.FirstOrDefault(x => x.Id == jobId);
                    if (job is not null)
                    {
                        job.ArchiveSha256 = expectedSha256.ToLowerInvariant();
                        if (target is not null) job.ResolvedDestination = target.Destination;
                        job.UpdateDate = DateTimeOffset.UtcNow;
                    }
                });
            }
            return new GatewayUploadResult(true, current, true, "Source-Transfer abgeschlossen.");
        }
        finally { gate.Release(); }
    }

    // Streams received source chunks directly onto a LocalFolder target's .partial with NO transfer-cache
    // copy. The resume offset is the physical .partial length (no in-memory state), and the final chunk
    // RE-READS the finished file to verify SHA-256 before publishing — identical, proven integrity semantics
    // to the cache path, just written straight to the target (no holes, so a whole-file read is valid).
    // Called with the per-transfer gate already held.
    private async Task<GatewayUploadResult> ReceiveSourceChunkToLocalTargetAsync(
        string transferId, long offset, bool final, long jobId, long totalBytes, string expectedSha256,
        StreamingPrimaryTarget target, Stream body, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target.Object.Location);
        var destination = target.Destination;
        var partial = destination + ".partial";

        // Already finalized (duplicate final chunk after completion). The destination is unique per job
        // (task-{TaskId}-{JobId}). Re-verify its SHA before re-acknowledging: normally the retry reset
        // already deleted any stale destination, but if that delete ever failed (e.g. a locked file) this
        // catches a stale/mismatched destination and forces a rebuild instead of stamping a new SHA over
        // old bytes — a loud stall rather than silent corruption.
        if (!File.Exists(partial) && File.Exists(destination))
        {
            var completedLength = new FileInfo(destination).Length;
            if (completedLength != offset)
                return new GatewayUploadResult(false, completedLength, false, "Der abgeschlossene Ziel-Checkpoint hat einen anderen Offset.");
            if (!final)
                return new GatewayUploadResult(true, completedLength, false, "Ziel ist bereits vollstaendig geschrieben.");
            if (completedLength != totalBytes || !ArchiveIntegrity.IsSha256(expectedSha256))
            {
                try { File.Delete(destination); } catch { }
                return new GatewayUploadResult(false, 0, false, "Abgeschlossenes Ziel passt nicht; der Transfer wird neu aufgebaut.");
            }
            try
            {
                await ArchiveIntegrity.VerifySha256Async(destination, expectedSha256, cancellationToken);
            }
            catch (InvalidDataException)
            {
                try { File.Delete(destination); } catch { }
                return new GatewayUploadResult(false, 0, false, "Abgeschlossenes Ziel hat eine falsche Pruefsumme; der Transfer wird neu aufgebaut.");
            }
            UpdatePipelineJob(jobId, transferId, completedLength, completedLength, totalBytes, destination);
            MarkStreamingTargetFinal(jobId, expectedSha256, destination);
            return new GatewayUploadResult(true, completedLength, true, "Ziel war bereits abgeschlossen und wurde bestaetigt.");
        }

        // Resume offset is the physical partial length — the single source of truth (survives restarts and
        // needs no in-memory state). A checkpoint mismatch tells the secondary to re-align.
        var current = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (current != offset)
            return new GatewayUploadResult(false, current, false, "Der Ziel-Checkpoint stimmt nicht mit der Primary ueberein.");

        if (!final)
        {
            archiveService.EnsureFreeSpace(target.Object.Location, 4 * 1024 * 1024);
            await using (var output = new FileStream(partial, FileMode.Append, FileAccess.Write, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await body.CopyToAsync(output, 4 * 1024 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            current = new FileInfo(partial).Length;
            UpdatePipelineJob(jobId, transferId, current, current, totalBytes, partial);
            return new GatewayUploadResult(true, current, false, "Ziel-Chunk gespeichert.");
        }

        // Final chunk (empty body): validate the physical size, then RE-READ and SHA-verify the actual file
        // before atomically publishing it. On any mismatch the partial is discarded so nothing corrupt lands.
        if (current != totalBytes || !ArchiveIntegrity.IsSha256(expectedSha256))
        {
            try { File.Delete(partial); } catch { }
            return new GatewayUploadResult(false, 0, false, "Finale Ziel-Metadaten sind ungueltig.");
        }
        try
        {
            await ArchiveIntegrity.VerifySha256Async(partial, expectedSha256, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            try { File.Delete(partial); } catch { }
            return new GatewayUploadResult(false, 0, false, ex.Message);
        }
        File.Move(partial, destination, overwrite: true);
        UpdatePipelineJob(jobId, transferId, current, current, totalBytes, destination);
        MarkStreamingTargetFinal(jobId, expectedSha256, destination);
        return new GatewayUploadResult(true, current, true, "Ziel-Transfer abgeschlossen.");
    }

    // SMB counterpart of ReceiveSourceChunkToLocalTargetAsync: writes chunks straight to the SMB target via
    // SMBLibrary (no local cache), with the durable REMOTE size as the offset and a remote whole-file SHA
    // re-read on the final chunk. Same state-free integrity semantics as the LocalFolder direct route.
    private async Task<GatewayUploadResult> ReceiveSourceChunkToSmbTargetAsync(
        string transferId, long offset, bool final, long jobId, long totalBytes, string expectedSha256,
        StreamingPrimaryTarget target, Stream body, CancellationToken cancellationToken)
    {
        var location = target.Object.Location;
        var credential = target.Credential;
        var name = target.FileName;

        var partialSize = await smbClient.GetRemoteFileSizeAsync(location, name + ".partial", credential, cancellationToken);

        // Already finalized (duplicate final after completion): no partial, but the finalized destination exists.
        if (partialSize == 0)
        {
            var destinationSize = await smbClient.GetRemoteFileSizeAsync(location, name, credential, cancellationToken);
            if (destinationSize > 0)
            {
                if (destinationSize != offset)
                    return new GatewayUploadResult(false, destinationSize, false, "Der abgeschlossene Ziel-Checkpoint hat einen anderen Offset.");
                if (!final)
                    return new GatewayUploadResult(true, destinationSize, false, "Ziel ist bereits vollstaendig geschrieben.");
                if (destinationSize != totalBytes || !ArchiveIntegrity.IsSha256(expectedSha256))
                {
                    await smbClient.DeleteRemoteFileAsync(location, name, credential, cancellationToken);
                    return new GatewayUploadResult(false, 0, false, "Abgeschlossenes Ziel passt nicht; der Transfer wird neu aufgebaut.");
                }
                try { await smbClient.VerifyRemoteSha256Async(location, name, expectedSha256, credential, cancellationToken); }
                catch (InvalidDataException)
                {
                    await smbClient.DeleteRemoteFileAsync(location, name, credential, cancellationToken);
                    return new GatewayUploadResult(false, 0, false, "Abgeschlossenes Ziel hat eine falsche Pruefsumme; der Transfer wird neu aufgebaut.");
                }
                UpdatePipelineJob(jobId, transferId, destinationSize, destinationSize, totalBytes, target.Destination);
                MarkStreamingTargetFinal(jobId, expectedSha256, target.Destination);
                return new GatewayUploadResult(true, destinationSize, true, "Ziel war bereits abgeschlossen und wurde bestaetigt.");
            }
        }

        if (partialSize != offset)
            return new GatewayUploadResult(false, partialSize, false, "Der Ziel-Checkpoint stimmt nicht mit der Primary ueberein.");

        if (!final)
        {
            // Read the HTTP body ASYNCHRONOUSLY here (Kestrel forbids synchronous stream reads); SMBLibrary's
            // blocking WriteFile then runs off-thread over the buffered bytes. The chunk is bounded (<= the
            // sender's ChunkSize), so buffering it in memory is safe and keeps no data on local disk.
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, 1024 * 1024, cancellationToken);
            var newSize = await smbClient.WriteRemoteChunkAsync(location, name, offset, buffer.GetBuffer(), (int)buffer.Length, credential, cancellationToken);
            UpdatePipelineJob(jobId, transferId, newSize, newSize, totalBytes, target.Destination + ".partial");
            return new GatewayUploadResult(true, newSize, false, "Ziel-Chunk gespeichert.");
        }

        // Final chunk (empty body): validate remote size, re-read + SHA-verify the remote file, then rename.
        if (partialSize != totalBytes || !ArchiveIntegrity.IsSha256(expectedSha256))
        {
            await smbClient.DeleteRemoteFileAsync(location, name + ".partial", credential, cancellationToken);
            return new GatewayUploadResult(false, 0, false, "Finale Ziel-Metadaten sind ungueltig.");
        }
        try { await smbClient.VerifyRemoteSha256Async(location, name + ".partial", expectedSha256, credential, cancellationToken); }
        catch (InvalidDataException ex)
        {
            await smbClient.DeleteRemoteFileAsync(location, name + ".partial", credential, cancellationToken);
            return new GatewayUploadResult(false, 0, false, ex.Message);
        }
        await smbClient.FinalizeStreamingRenameAsync(location, name, credential, cancellationToken);
        UpdatePipelineJob(jobId, transferId, partialSize, partialSize, totalBytes, target.Destination);
        MarkStreamingTargetFinal(jobId, expectedSha256, target.Destination);
        return new GatewayUploadResult(true, partialSize, true, "Ziel-Transfer abgeschlossen.");
    }

    private void MarkStreamingTargetFinal(long jobId, string expectedSha256, string destination)
    {
        if (jobId <= 0) return;
        store.Update(data =>
        {
            var job = data.TransferJobs.FirstOrDefault(x => x.Id == jobId);
            if (job is null) return;
            job.ArchiveSha256 = expectedSha256.ToLowerInvariant();
            job.ResolvedDestination = destination;
            job.UpdateDate = DateTimeOffset.UtcNow;
        });
    }

    public Task<string> ApplyTargetArchiveAsync(string archivePath, string transferId, GatewayTargetRequest target, CancellationToken cancellationToken) => StoreTargetAsync(archivePath, transferId, target, cancellationToken);

    public string GetRestorePackagePath(string transferId)
    {
        EnsureTransferId(transferId);
        return Path.Combine(archiveService.CacheDirectory, $"restore-package-{transferId}.tar");
    }

    public string ResolveOutgoingTargetArchive(string transferId, long taskId)
    {
        var restorePackage = GetRestorePackagePath(transferId);
        if (File.Exists(restorePackage)) return restorePackage;
        return Path.Combine(archiveService.CacheDirectory, $"task-{taskId}-{transferId}.archive");
    }

    public async Task<GatewayUploadResult> ReceiveUploadAsync(string transferId, GatewayTargetRequest target, long offset, bool final, Stream body, CancellationToken cancellationToken)
    {
        EnsureTransferId(transferId);
        var gate = _transferLocks.GetOrAdd($"upload-{transferId}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var partialPath = UploadPartialPath(transferId);
            Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
            var currentOffset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (currentOffset != offset)
                return new GatewayUploadResult(false, currentOffset, false, "Der Upload-Checkpoint stimmt nicht mit der Secondary überein.");

            archiveService.EnsureCacheFreeSpace(0);
            await using (var output = new FileStream(partialPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await body.CopyToAsync(output, cancellationToken);
            }

            currentOffset = new FileInfo(partialPath).Length;
            if (!final) return new GatewayUploadResult(true, currentOffset, false, "Chunk gespeichert.");

            if (!ArchiveIntegrity.IsSha256(target.Sha256))
            {
                File.Delete(partialPath);
                return new GatewayUploadResult(false, 0, false, "Upload verworfen: SHA-256-Prüfsumme fehlt.");
            }
            try
            {
                await ArchiveIntegrity.VerifySha256Async(partialPath, target.Sha256, cancellationToken);
            }
            catch (InvalidDataException ex)
            {
                File.Delete(partialPath);
                return new GatewayUploadResult(false, 0, false, ex.Message);
            }

            var archivePath = UploadFinalPath(transferId);
            File.Move(partialPath, archivePath, overwrite: true);
            try
            {
                var destination = await StoreTargetAsync(archivePath, transferId, target, cancellationToken);
                File.Delete(archivePath);
                return new GatewayUploadResult(true, currentOffset, true, $"Transfer abgeschlossen: {destination}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Gateway target application failed for transfer {TransferId}", transferId);
                return new GatewayUploadResult(false, currentOffset, false, $"Ziel konnte nicht geschrieben werden: {ex.Message}");
            }
        }
        finally { gate.Release(); }
    }

    private async Task<string> StoreTargetAsync(string archivePath, string transferId, GatewayTargetRequest target, CancellationToken cancellationToken)
    {
        var fileName = $"task-{target.TaskId}-{transferId}{ArchiveExtension(target.Compression)}";
        if (target.Kind == ObjectKind.LocalFolder)
        {
            Directory.CreateDirectory(target.Location);
            var destination = Path.Combine(target.Location, fileName);
            await CopyFileResumableAsync(archivePath, destination, target.Sha256, cancellationToken);
            return destination;
        }

        if (target.Kind == ObjectKind.Smb)
        {
            (string Username, string Password)? credential = string.IsNullOrWhiteSpace(target.SmbUsername) || target.SmbPassword is null ? null : (target.SmbUsername!, target.SmbPassword!);
            await smbClient.UploadFileAsync(target.Location, archivePath, fileName, credential, cancellationToken);
            return $"{target.Location.TrimEnd('\\', '/')}/{fileName}";
        }

        throw new InvalidOperationException($"Der Ziel-Object-Typ {target.Kind} wird auf der Secondary noch nicht unterstützt.");
    }

    private StreamingPrimaryTarget? ResolveStreamingPrimaryTarget(long jobId)
    {
        var data = store.Read();
        var job = data.TransferJobs.FirstOrDefault(item => item.Id == jobId);
        if (job is null || job.Method != BackupMethod.Full) return null;
        var target = data.Objects.FirstOrDefault(item => item.Id == job.TargetObjectId);
        if (target is null || target.Kind is not (ObjectKind.LocalFolder or ObjectKind.Smb)) return null;
        var instance = data.Instances.FirstOrDefault(item => item.Id == target.InstanceId);
        if (instance?.Role != InstanceRole.Primary) return null;

        var fileName = $"task-{job.TaskId}-{job.Id}{ArchiveExtension(job.Compression)}";
        var destination = target.Kind == ObjectKind.LocalFolder
            ? Path.Combine(target.Location, fileName)
            : $"{target.Location.TrimEnd('\\', '/')}/{fileName}";
        return new StreamingPrimaryTarget(target, store.GetSmbCredential(target.Id), fileName, destination);
    }

    private async Task<long> SyncStreamingTargetAsync(
        StreamingPrimaryTarget target,
        string sourcePath,
        bool final,
        CancellationToken cancellationToken)
    {
        if (target.Object.Kind == ObjectKind.LocalFolder)
        {
            Directory.CreateDirectory(target.Object.Location);
            var partial = target.Destination + ".partial";
            await CopyAvailableTailAsync(sourcePath, partial, cancellationToken);
            var written = new FileInfo(partial).Length;
            if (final) File.Move(partial, target.Destination, overwrite: true);
            return written;
        }

        var uploaded = await smbClient.SyncPartialFileAsync(target.Object.Location, sourcePath, target.FileName, target.Credential, cancellationToken);
        if (final) await smbClient.FinalizePartialFileAsync(target.Object.Location, target.FileName, target.Credential, cancellationToken);
        return uploaded;
    }

    private async Task ResetStreamingTargetAsync(StreamingPrimaryTarget? target, CancellationToken cancellationToken)
    {
        if (target is null) return;
        try
        {
            if (target.Object.Kind == ObjectKind.LocalFolder)
            {
                var partial = target.Destination + ".partial";
                if (File.Exists(partial)) File.Delete(partial);
                // Also drop a finalized destination from a prior failed attempt of the SAME job (the filename
                // is job-unique task-{TaskId}-{JobId}). Otherwise a retry that rebuilds a byte-different source
                // would re-acknowledge stale bytes under the new SHA (silent corruption) or stall. The direct
                // route has no full cache to re-verify against, so the clean-slate reset is the safety net.
                if (File.Exists(target.Destination)) File.Delete(target.Destination);
                return;
            }
            if (SmbClientService.IsStreamingEnabled)
            {
                // Direct route uses SMBLibrary end to end (no smbclient CLI): drop both the .partial and a
                // finalized destination from a prior failed attempt so a rebuilt source cannot be
                // re-acknowledged under a new SHA (same clean-slate rule as LocalFolder).
                await smbClient.DeleteRemoteFileAsync(target.Object.Location, target.FileName + ".partial", target.Credential, cancellationToken);
                await smbClient.DeleteRemoteFileAsync(target.Object.Location, target.FileName, target.Credential, cancellationToken);
            }
            else
            {
                await smbClient.DeleteUploadPartialAsync(target.Object.Location, target.FileName, target.Credential, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Streaming target checkpoint cleanup failed for {Destination}", target.Destination);
        }
    }

    private void UpdatePipelineJob(long jobId, string transferId, long transferred, long written, long total, string checkpoint)
    {
        if (jobId <= 0) return;
        var rate = _pipelineRates.GetOrAdd(transferId, _ => new PipelineRateState());
        var transferSpeed = rate.Transfer.Sample(transferred);
        var writeSpeed = rate.Write.Sample(written);
        store.Update(data =>
        {
            var job = data.TransferJobs.FirstOrDefault(item => item.Id == jobId);
            if (job is null) return;
            // Never resurrect a job that is already terminal or has a pending cancel: a late in-flight source
            // chunk must not flip an "Abgebrochen"/"Fehler"/"Completed" job back to "Running".
            if (job.CancelRequested || job.State is "Abgebrochen" or "Fehler" or "Failed" or "Completed") return;
            job.State = "Running";
            job.Phase = written < transferred ? JobPhase.Writing : JobPhase.Transferring;
            job.BytesTransferred = transferred;
            job.BytesWritten = written;
            if (total > 0) job.TotalBytes = total;
            job.SpeedBytesPerSecond = transferSpeed;
            job.WriteSpeedBytesPerSecond = writeSpeed;
            job.CheckpointPath = checkpoint;
            job.UpdateDate = DateTimeOffset.UtcNow;
        });
    }

    private static async Task CopyAvailableTailAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var sourceLength = new FileInfo(sourcePath).Length;
        var offset = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;
        if (offset > sourceLength)
        {
            File.Delete(destinationPath);
            offset = 0;
        }
        if (offset == sourceLength) return;

        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destinationPath, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        input.Position = offset;
        await input.CopyToAsync(output, 4 * 1024 * 1024, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private string UploadPartialPath(string transferId) => Path.Combine(archiveService.CacheDirectory, $"gateway-upload-{transferId}.tar.partial");
    private string UploadFinalPath(string transferId) => Path.Combine(archiveService.CacheDirectory, $"gateway-upload-{transferId}.tar");
    private string MetricsPath(string transferId) => Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.metrics.json");
    private static string ArchiveExtension(BackupCompression compression) => compression == BackupCompression.None ? ".tar" : ".tar.br";

    private sealed record StreamingPrimaryTarget(BackupObject Object, (string Username, string Password)? Credential, string FileName, string Destination);

    private sealed class PipelineRateState
    {
        public SpeedWindow Transfer { get; } = new();
        public SpeedWindow Write { get; } = new();
    }

    private static void EnsureTransferId(string transferId)
    {
        if (!Guid.TryParse(transferId, out _)) throw new ArgumentException("Ungültige Transfer-ID.", nameof(transferId));
    }

    private static async Task CopyFileResumableAsync(string sourcePath, string destinationPath, string expectedSha256, CancellationToken cancellationToken)
    {
        var sourceLength = new FileInfo(sourcePath).Length;
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length == sourceLength)
        {
            try
            {
                await ArchiveIntegrity.VerifySha256Async(destinationPath, expectedSha256, cancellationToken);
                return;
            }
            catch (InvalidDataException) { File.Delete(destinationPath); }
        }
        var partialPath = destinationPath + ".partial";
        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (offset > sourceLength)
        {
            File.Delete(partialPath);
            offset = 0;
        }

        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(partialPath, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            input.Position = offset;
            await input.CopyToAsync(output, 4 * 1024 * 1024, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        File.Move(partialPath, destinationPath, overwrite: true);
        await ArchiveIntegrity.VerifySha256Async(destinationPath, expectedSha256, cancellationToken);
    }

    private sealed class RangeReadStream(Stream inner, long start, long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, length); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_position >= length) return 0;
            inner.Position = start + _position;
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, length - _position)]);
            _position += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= length) return ValueTask.FromResult(0);
            inner.Position = start + _position;
            var allowed = buffer[..(int)Math.Min(buffer.Length, length - _position)];
            return ReadAsyncCore(allowed, cancellationToken);
        }
        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => length + offset, _ => _position };
            Position = next;
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
