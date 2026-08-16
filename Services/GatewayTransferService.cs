using System.Collections.Concurrent;
using System.Text.Json;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed record GatewaySourceRequest(string TransferId, ObjectKind Kind, string Location, string? SmbUsername, string? SmbPassword, long Offset = 0, BackupCompression Compression = BackupCompression.None, IReadOnlyList<string>? IncludedPaths = null);
public sealed record GatewayTargetRequest(long TaskId, ObjectKind Kind, string Location, string? SmbUsername, string? SmbPassword, BackupCompression Compression = BackupCompression.None, string Sha256 = "");
public sealed record GatewayUploadResult(bool Success, long Offset, bool Completed, string Message);
public sealed record GatewayArchiveMetrics(long SourceBytes, long StoredBytes, string Sha256 = "");

public sealed class GatewayTransferService(
    ArchiveService archiveService,
    DockerConsistencyService dockerConsistency,
    SmbClientService smbClient,
    PersistentStore store,
    ILogger<GatewayTransferService> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _transferLocks = new(StringComparer.OrdinalIgnoreCase);

    public Task<string> PrepareSourceArchiveAsync(GatewaySourceRequest request, CancellationToken cancellationToken) =>
        PrepareSourceArchiveAsync(request, new BackupConsistencySettings(BackupConsistencyMode.None, "", "", "", 60), cancellationToken);

    public async Task<string> PrepareSourceArchiveAsync(GatewaySourceRequest request, BackupConsistencySettings consistency, CancellationToken cancellationToken)
    {
        EnsureTransferId(request.TransferId);
        var gate = _transferLocks.GetOrAdd(request.TransferId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var archivePath = Path.Combine(archiveService.CacheDirectory, $"gateway-source-{request.TransferId}.tar");
            if (File.Exists(archivePath)) return archivePath;

            var buildingPath = archivePath + ".building";
            if (File.Exists(buildingPath)) File.Delete(buildingPath);
            var source = new BackupObject { Kind = request.Kind, Location = request.Location };
            (string Username, string Password)? credential = string.IsNullOrWhiteSpace(request.SmbUsername) || request.SmbPassword is null ? null : (request.SmbUsername!, request.SmbPassword!);
            DockerConsistencyLease? lease = null;
            try
            {
                if (consistency.Mode != BackupConsistencyMode.None)
                    lease = await dockerConsistency.BeginAsync(consistency, cancellationToken);
                var result = await archiveService.CreateCompressedAsync(source, credential, buildingPath, request.Compression, null, cancellationToken, request.IncludedPaths);
                File.Move(buildingPath, archivePath, overwrite: true);
                var sha256 = await ArchiveIntegrity.ComputeSha256Async(archivePath, cancellationToken);
                await File.WriteAllTextAsync(MetricsPath(request.TransferId), JsonSerializer.Serialize(new GatewayArchiveMetrics(result.SourceBytes, result.StoredBytes, sha256)), cancellationToken);
                return archivePath;
            }
            finally
            {
                if (lease is not null) await dockerConsistency.EndAsync(consistency, lease, CancellationToken.None);
            }
        }
        finally { gate.Release(); }
    }

    public long GetUploadOffset(string transferId)
    {
        EnsureTransferId(transferId);
        var path = UploadPartialPath(transferId);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public Stream OpenSourceRange(string archivePath, long offset)
    {
        var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new RangeReadStream(file, offset, file.Length - offset);
    }

    public async Task<long> GetSourceOffsetAsync(string transferId, string expectedSha256, CancellationToken cancellationToken)
    {
        EnsureTransferId(transferId);
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
            var partial = Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.tar.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            var current = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (current != offset) return new GatewayUploadResult(false, current, false, "Der Source-Checkpoint stimmt nicht mit der Primary überein.");
            await using (var output = new FileStream(partial, FileMode.Append, FileAccess.Write, FileShare.None)) await body.CopyToAsync(output, cancellationToken);
            current = new FileInfo(partial).Length;
            if (jobId > 0)
            {
                store.Update(data =>
                {
                    var job = data.TransferJobs.FirstOrDefault(x => x.Id == jobId);
                    if (job is not null) { job.State = "Running"; job.BytesTransferred = current; job.TotalBytes = totalBytes; job.CheckpointPath = partial; job.UpdateDate = DateTimeOffset.UtcNow; }
                });
            }
            if (!final) return new GatewayUploadResult(true, current, false, "Source-Chunk gespeichert.");
            if (current != totalBytes || !ArchiveIntegrity.IsSha256(expectedSha256))
            {
                File.Delete(partial);
                return new GatewayUploadResult(false, 0, false, "Source-Transfer verworfen: Größe oder SHA-256-Prüfsumme fehlt.");
            }
            try
            {
                await ArchiveIntegrity.VerifySha256Async(partial, expectedSha256, cancellationToken);
            }
            catch (InvalidDataException ex)
            {
                File.Delete(partial);
                return new GatewayUploadResult(false, 0, false, ex.Message);
            }
            var archive = Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.tar");
            File.Move(partial, archive, overwrite: true);
            if (jobId > 0)
            {
                store.Update(data =>
                {
                    var job = data.TransferJobs.FirstOrDefault(x => x.Id == jobId);
                    if (job is not null) { job.ArchiveSha256 = expectedSha256.ToLowerInvariant(); job.UpdateDate = DateTimeOffset.UtcNow; }
                });
            }
            return new GatewayUploadResult(true, current, true, "Source-Transfer abgeschlossen.");
        }
        finally { gate.Release(); }
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

    private string UploadPartialPath(string transferId) => Path.Combine(archiveService.CacheDirectory, $"gateway-upload-{transferId}.tar.partial");
    private string UploadFinalPath(string transferId) => Path.Combine(archiveService.CacheDirectory, $"gateway-upload-{transferId}.tar");
    private string MetricsPath(string transferId) => Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.metrics.json");
    private static string ArchiveExtension(BackupCompression compression) => compression == BackupCompression.None ? ".tar" : ".tar.br";

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
