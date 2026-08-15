using System.Formats.Tar;
using System.IO.Compression;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed record SecondaryArchiveExportPayload(
    ObjectKind Kind,
    string Location,
    string Destination,
    string? SmbUsername,
    string? SmbPassword);

public sealed record RestoreBrowserEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTimeOffset? ModifiedDate);

public sealed record RestoreFileHandle(Stream Stream, string DownloadName, long Length);

public sealed class RestoreArchiveService(
    ArchiveService archiveService,
    SmbClientService smbClient,
    ReverseIncrementalRepositoryService incrementalRepository,
    PersistentStore store,
    SecondaryCommandService commands)
{
    public async Task<IReadOnlyList<RestoreBrowserEntry>> BrowseAsync(
        TransferJob job,
        string folder,
        CancellationToken cancellationToken)
    {
        var archivePath = await EnsureArchiveAvailableAsync(job, cancellationToken);
        var normalizedFolder = NormalizeFolder(folder);
        var prefix = string.IsNullOrEmpty(normalizedFolder) ? "" : normalizedFolder + "/";
        var entries = new Dictionary<string, RestoreBrowserEntry>(StringComparer.OrdinalIgnoreCase);

        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new TarReader(input, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNormalizeEntryPath(entry.Name, out var entryPath)) continue;
            if (!entryPath.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var remainder = entryPath[prefix.Length..];
            if (string.IsNullOrEmpty(remainder)) continue;
            var separator = remainder.IndexOf('/');
            var childName = separator < 0 ? remainder : remainder[..separator];
            var childPath = string.IsNullOrEmpty(normalizedFolder) ? childName : $"{normalizedFolder}/{childName}";
            var isDirectory = separator >= 0 || entry.EntryType == TarEntryType.Directory;
            if (!isDirectory && entry.DataStream is null) continue;

            var candidate = new RestoreBrowserEntry(
                childName,
                childPath,
                isDirectory,
                isDirectory ? 0 : entry.Length,
                entry.ModificationTime);

            if (!entries.TryGetValue(childName, out var existing) || existing.IsDirectory)
                entries[childName] = candidate;
        }

        return entries.Values
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<RestoreFileHandle> OpenFileAsync(
        TransferJob job,
        string entryPath,
        long userId,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeEntryPath(entryPath, out var normalizedPath))
            throw new InvalidOperationException("Der ausgewählte Archivpfad ist ungültig.");

        var archivePath = await EnsureArchiveAvailableAsync(job, cancellationToken);
        var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new TarReader(input, leaveOpen: false);
        try
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryNormalizeEntryPath(entry.Name, out var candidate) || candidate != normalizedPath) continue;
                if (entry.EntryType == TarEntryType.Directory || entry.DataStream is null)
                    throw new InvalidOperationException("Der ausgewählte Eintrag ist keine wiederherstellbare Datei.");

                var stream = new OwnedRestoreStream(entry.DataStream, reader);
                RecordDownload(job.Id, normalizedPath, entry.Length, userId);
                return new RestoreFileHandle(stream, normalizedPath.Split('/').Last(), entry.Length);
            }
        }
        catch
        {
            reader.Dispose();
            throw;
        }

        reader.Dispose();
        throw new FileNotFoundException($"Die Datei '{normalizedPath}' wurde in dieser Backupversion nicht gefunden.");
    }

    public async Task<string> EnsureArchiveAvailableAsync(TransferJob job, CancellationToken cancellationToken)
    {
        if (!job.State.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Nur erfolgreich abgeschlossene Jobs können durchsucht werden.");
        if (string.IsNullOrWhiteSpace(job.ResolvedDestination))
            throw new InvalidOperationException("Für diesen Job wurde kein tatsächlicher Archivpfad protokolliert.");
        if (!Enum.TryParse<ObjectKind>(job.TargetObjectKind, true, out var targetKind))
            throw new InvalidOperationException($"Der Zieltyp '{job.TargetObjectKind}' kann nicht gelesen werden.");

        if (BackupMethodPolicy.IsChunked(job.Method))
            return await EnsureIncrementalArchiveAvailableAsync(job, targetKind, cancellationToken);

        var data = store.Read();
        var instance = data.Instances.FirstOrDefault(item => item.Id == job.TargetInstanceId);
        var isSecondary = instance?.Role == InstanceRole.Secondary ||
            instance is null && !job.TargetInstanceName.Equals("Primary", StringComparison.OrdinalIgnoreCase);

        if (!isSecondary && targetKind == ObjectKind.LocalFolder)
        {
            var destination = EnsurePathWithin(job.TargetLocation, job.ResolvedDestination);
            if (!File.Exists(destination)) throw new FileNotFoundException("Das Backup-Archiv wurde am protokollierten Ziel nicht gefunden.", destination);
            return await EnsureDecompressedAsync(job, destination, cancellationToken);
        }

        var cachePath = RestoreCachePath(job.Id);
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0) return cachePath;
        var transferCachePath = job.Compression == BackupCompression.None ? cachePath : cachePath + ".br";

        var credential = job.TargetObjectId == 0 ? null : store.GetSmbCredential(job.TargetObjectId);
        if (!isSecondary)
        {
            if (targetKind != ObjectKind.Smb)
                throw new InvalidOperationException($"Restore aus dem Zieltyp '{targetKind}' wird noch nicht unterstützt.");
            await DownloadSmbArchiveAsync(job.TargetLocation, job.ResolvedDestination, transferCachePath, credential, cancellationToken);
            return await EnsureDecompressedAsync(job, transferCachePath, cancellationToken);
        }

        if (instance is null)
            throw new InvalidOperationException($"Die Zielinstanz '{job.TargetInstanceName}' existiert nicht mehr.");

        var transferId = Guid.NewGuid().ToString("N");
        var payload = new SecondaryArchiveExportPayload(
            targetKind,
            job.TargetLocation,
            job.ResolvedDestination,
            credential?.Username,
            credential?.Password);
        var commandId = commands.Queue(instance.Id, SecondaryCommandKind.ExportArchive, transferId, payload);
        var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
        if (command.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte das Backup-Archiv nicht bereitstellen." : command.Error);

        var incoming = Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.tar");
        if (!File.Exists(incoming))
            throw new FileNotFoundException("Die Secondary meldete Erfolg, aber das Restore-Archiv fehlt auf der Primary.", incoming);
        Directory.CreateDirectory(Path.GetDirectoryName(transferCachePath)!);
        File.Move(incoming, transferCachePath, overwrite: true);
        return await EnsureDecompressedAsync(job, transferCachePath, cancellationToken);
    }

    private async Task<string> EnsureIncrementalArchiveAvailableAsync(
        TransferJob job,
        ObjectKind targetKind,
        CancellationToken cancellationToken)
    {
        var data = store.Read();
        var snapshot = data.BackupSnapshots.FirstOrDefault(item => item.Id == job.SnapshotId || item.TransferJobId == job.Id)
            ?? throw new InvalidOperationException("Die Snapshot-Metadaten dieses Reverse-Incremental-Jobs fehlen.");
        var instance = data.Instances.FirstOrDefault(item => item.Id == job.TargetInstanceId);
        var isSecondary = instance?.Role == InstanceRole.Secondary ||
            instance is null && !job.TargetInstanceName.Equals("Primary", StringComparison.OrdinalIgnoreCase);
        var cachePath = RestoreCachePath(job.Id);
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0) return cachePath;

        var task = new BackupTask
        {
            Id = job.TaskId,
            Token = job.TaskToken,
            Name = job.TaskName,
            Method = job.Method
        };
        var target = new BackupObject
        {
            Id = job.TargetObjectId,
            Kind = targetKind,
            Direction = ObjectDirection.Target,
            Location = job.TargetLocation,
            InstanceId = job.TargetInstanceId
        };
        var credential = job.TargetObjectId == 0 ? null : store.GetSmbCredential(job.TargetObjectId);
        if (!isSecondary)
            return await incrementalRepository.CreateSnapshotArchiveAsync(task, target, snapshot.Token, credential, cachePath, cancellationToken);

        if (instance is null)
            throw new InvalidOperationException($"Die Zielinstanz '{job.TargetInstanceName}' existiert nicht mehr.");
        var transferId = Guid.NewGuid().ToString("N");
        var targetRequest = new GatewayTargetRequest(task.Id, target.Kind, target.Location, credential?.Username, credential?.Password);
        var payload = new IncrementalSnapshotExportPayload(task.Id, task.Token, snapshot.Token, targetRequest);
        var commandId = commands.Queue(instance.Id, SecondaryCommandKind.ExportIncrementalSnapshot, transferId, payload);
        var command = await commands.WaitForCompletionAsync(commandId, cancellationToken);
        if (command.State != "Completed")
            throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "Die Secondary konnte den Reverse-Incremental-Snapshot nicht materialisieren." : command.Error);
        var incoming = Path.Combine(archiveService.CacheDirectory, $"gateway-source-{transferId}.tar");
        if (!File.Exists(incoming))
            throw new FileNotFoundException("Die Secondary meldete Erfolg, aber das materialisierte Snapshot-Archiv fehlt.", incoming);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.Move(incoming, cachePath, overwrite: true);
        return cachePath;
    }

    public async Task<(string ArchivePath, bool DeleteAfter)> PrepareSecondaryExportAsync(
        SecondaryArchiveExportPayload payload,
        string transferId,
        CancellationToken cancellationToken)
    {
        if (payload.Kind == ObjectKind.LocalFolder)
        {
            var destination = EnsurePathWithin(payload.Location, payload.Destination);
            if (!File.Exists(destination)) throw new FileNotFoundException("Das Backup-Archiv wurde auf der Secondary nicht gefunden.", destination);
            return (destination, false);
        }

        if (payload.Kind == ObjectKind.Smb)
        {
            var outputPath = Path.Combine(archiveService.CacheDirectory, $"restore-export-{transferId}.tar");
            (string Username, string Password)? credential = string.IsNullOrWhiteSpace(payload.SmbUsername) || payload.SmbPassword is null
                ? null
                : (payload.SmbUsername!, payload.SmbPassword!);
            await DownloadSmbArchiveAsync(payload.Location, payload.Destination, outputPath, credential, cancellationToken);
            return (outputPath, true);
        }

        throw new InvalidOperationException($"Restore aus dem Zieltyp '{payload.Kind}' wird auf der Secondary noch nicht unterstützt.");
    }

    public static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";
        if (!TryNormalizeEntryPath(folder, out var normalized))
            throw new InvalidOperationException("Der ausgewählte Ordnerpfad ist ungültig.");
        return normalized;
    }

    private async Task DownloadSmbArchiveAsync(
        string location,
        string destination,
        string outputPath,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var remoteName = GetArchiveFileName(destination);
        var partialPath = outputPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await smbClient.DownloadFileAsync(location, remoteName, partialPath, credential, cancellationToken);
        File.Move(partialPath, outputPath, overwrite: true);
    }

    private void RecordDownload(long jobId, string entryPath, long length, long userId)
    {
        store.Update(data =>
        {
            if (!data.TransferJobs.Any(job => job.Id == jobId)) return;
            var now = DateTimeOffset.UtcNow;
            data.JobSteps.Add(new JobStep
            {
                Id = store.NextId(data.JobSteps.Select(step => step.Id)),
                TransferJobId = jobId,
                Sequence = data.JobSteps.Where(step => step.TransferJobId == jobId).Select(step => step.Sequence).DefaultIfEmpty().Max() + 1,
                Stage = "Restore",
                State = "Downloaded",
                Message = $"Datei '{entryPath}' wurde aus dieser Backupversion wiederhergestellt.",
                InstanceName = "Primary",
                Location = entryPath,
                BytesTransferred = length,
                TotalBytes = length,
                CreateDate = now,
                CreateUserId = userId,
                UpdateDate = now,
                UpdateUserId = userId
            });
        });
    }

    private string RestoreCachePath(long jobId) => Path.Combine(archiveService.CacheDirectory, "restore", $"job-{jobId}.tar");

    private async Task<string> EnsureDecompressedAsync(TransferJob job, string archivePath, CancellationToken cancellationToken)
    {
        if (job.Compression == BackupCompression.None) return archivePath;
        var outputPath = RestoreCachePath(job.Id);
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0) return outputPath;
        var partialPath = outputPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        try
        {
            await using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var decompressor = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false))
            await using (var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await decompressor.CopyToAsync(output, 4 * 1024 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(partialPath, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
        }
    }

    private static string GetArchiveFileName(string destination)
    {
        var normalized = destination.Replace('\\', '/').TrimEnd('/');
        var fileName = normalized.Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['\\', '/', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Der protokollierte SMB-Archivname ist ungültig.");
        return fileName;
    }

    private static string EnsurePathWithin(string baseDirectory, string candidate)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || string.IsNullOrWhiteSpace(candidate))
            throw new InvalidOperationException("Der protokollierte Archivpfad ist unvollständig.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Der Archivpfad liegt außerhalb des protokollierten Backup-Ziels.");
        return fullCandidate;
    }

    public static bool TryNormalizeEntryPath(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Replace('\\', '/').Trim();
        if (candidate.StartsWith('/') || candidate.Contains('\0')) return false;
        while (candidate.StartsWith("./", StringComparison.Ordinal)) candidate = candidate[2..];
        candidate = candidate.Trim('/');
        if (string.IsNullOrEmpty(candidate)) return false;
        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) return false;
        normalized = string.Join('/', segments);
        return true;
    }

    private sealed class OwnedRestoreStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
