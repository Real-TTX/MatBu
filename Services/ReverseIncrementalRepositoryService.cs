using System.Buffers;
using System.Formats.Tar;
using System.Security.Cryptography;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed class ReverseIncrementalRepositoryService(
    IHostEnvironment environment,
    IncrementalSourceService sources,
    SmbClientService smbClient,
    PersistentStore store,
    ILogger<ReverseIncrementalRepositoryService> logger)
{
    private readonly string _dataPath = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? Path.Combine(environment.ContentRootPath, "data");

    public string BuildRepositoryKey(BackupTask task, BackupObject target, MatBuInstance instance) =>
        $"{task.Token}|{instance.Id}|{target.Id}|{target.Kind}|{target.Location.TrimEnd('\\', '/')}";

    public string RepositoryRelativeBase(BackupTask task) => $"MatBu/task-{task.Id}-{task.Token[..Math.Min(8, task.Token.Length)]}";

    public async Task<IncrementalBackupManifest?> LoadPreviousManifestAsync(string taskToken, CancellationToken cancellationToken)
    {
        var latest = CatalogLatestPath(taskToken);
        return File.Exists(latest) ? await IncrementalManifestJson.ReadAsync(latest, cancellationToken) : null;
    }

    public async Task<IncrementalBackupManifest?> LoadBaselineManifestAsync(string taskToken, CancellationToken cancellationToken)
    {
        var baseline = CatalogBaselinePath(taskToken);
        return File.Exists(baseline) ? await IncrementalManifestJson.ReadAsync(baseline, cancellationToken) : null;
    }

    public async Task<IncrementalBackupManifest> LoadSnapshotManifestAsync(string taskToken, string snapshotToken, CancellationToken cancellationToken)
    {
        var path = CatalogSnapshotPath(taskToken, snapshotToken);
        if (!File.Exists(path)) throw new FileNotFoundException($"Der Reverse-Incremental-Snapshot '{snapshotToken}' wurde nicht gefunden.", path);
        return await IncrementalManifestJson.ReadAsync(path, cancellationToken);
    }

    public async Task<string> CreateSnapshotArchiveAsync(
        BackupTask task,
        BackupObject target,
        string snapshotToken,
        (string Username, string Password)? credential,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var selected = await LoadSnapshotManifestAsync(task.Token, snapshotToken, cancellationToken);
        var latest = await LoadPreviousManifestAsync(task.Token, cancellationToken)
            ?? throw new InvalidDataException("Das Latest-Manifest des Reverse-Incremental-Repositories fehlt.");
        if (!selected.RepositoryKey.Equals(latest.RepositoryKey, StringComparison.Ordinal))
            throw new InvalidDataException("Snapshot und aktuelles Repository gehören nicht zum selben Ziel.");

        var workingRoot = Path.Combine(_dataPath, "transfer-cache", "restore-incremental", Guid.NewGuid().ToString("N"));
        var treeRoot = Path.Combine(workingRoot, "tree");
        Directory.CreateDirectory(treeRoot);
        try
        {
            if (target.Kind == ObjectKind.LocalFolder)
                await MaterializeLocalSnapshotAsync(task, target, selected, latest, treeRoot, cancellationToken);
            else if (target.Kind == ObjectKind.Smb)
                await MaterializeSmbSnapshotAsync(task, target, selected, latest, treeRoot, workingRoot, credential, cancellationToken);
            else
                throw new InvalidOperationException($"Reverse-Incremental-Restore aus '{target.Kind}' wird noch nicht unterstützt.");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var partial = outputPath + ".partial";
            TryDelete(partial);
            await Task.Run(() => TarFile.CreateFromDirectory(treeRoot, partial, includeBaseDirectory: false), cancellationToken);
            File.Move(partial, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    public async Task<IncrementalApplyResult> ApplyAsync(
        BackupTask task,
        BackupObject target,
        TransferJob job,
        IncrementalBackupManifest manifest,
        IncrementalBackupManifest? previous,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        await sources.VerifyChangedChunksAsync(manifest, job.TransferId, cancellationToken);
        var result = target.Kind switch
        {
            ObjectKind.LocalFolder => await ApplyLocalAsync(task, target, job, manifest, previous, cancellationToken),
            ObjectKind.Smb => await ApplySmbAsync(task, target, job, manifest, previous, credential, cancellationToken),
            _ => throw new InvalidOperationException($"Reverse Incremental unterstützt das Ziel '{target.Kind}' noch nicht.")
        };
        await SaveCatalogAsync(task.Token, manifest, cancellationToken);
        RecordSnapshot(task, job, manifest, result);
        return result;
    }

    public async Task<IncrementalApplyResult> ApplyWithoutPrimaryCatalogAsync(
        BackupTask task,
        BackupObject target,
        TransferJob job,
        IncrementalBackupManifest manifest,
        IncrementalBackupManifest? previous,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        await sources.VerifyChangedChunksAsync(manifest, job.TransferId, cancellationToken);
        var result = target.Kind switch
        {
            ObjectKind.LocalFolder => await ApplyLocalAsync(task, target, job, manifest, previous, cancellationToken),
            ObjectKind.Smb => await ApplySmbAsync(task, target, job, manifest, previous, credential, cancellationToken),
            _ => throw new InvalidOperationException($"Reverse Incremental unterstützt das Ziel '{target.Kind}' noch nicht.")
        };
        await SaveCatalogAsync(task.Token, manifest, cancellationToken);
        return result;
    }

    public async Task SaveCatalogAndRecordAsync(
        BackupTask task,
        TransferJob job,
        IncrementalBackupManifest manifest,
        IncrementalApplyResult result,
        CancellationToken cancellationToken)
    {
        await SaveCatalogAsync(task.Token, manifest, cancellationToken);
        RecordSnapshot(task, job, manifest, result);
    }

    private async Task<IncrementalApplyResult> ApplyLocalAsync(
        BackupTask task,
        BackupObject target,
        TransferJob job,
        IncrementalBackupManifest manifest,
        IncrementalBackupManifest? previous,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(target.Location);
        Directory.CreateDirectory(root);
        var repositoryBase = EnsureWithin(root, Path.Combine(root, RepositoryRelativeBase(task).Replace('/', Path.DirectorySeparatorChar)));
        var currentRoot = Path.Combine(repositoryBase, "current");
        var metadataRoot = Path.Combine(repositoryBase, ".matbu");
        var chunkRoot = Path.Combine(metadataRoot, "chunks");
        var stagingRoot = Path.Combine(metadataRoot, "staging", job.TransferId);
        var manifestPath = Path.Combine(metadataRoot, "manifests", manifest.SnapshotToken + ".json");
        Directory.CreateDirectory(currentRoot);
        Directory.CreateDirectory(chunkRoot);
        Directory.CreateDirectory(stagingRoot);
        await WriteJournalAsync(Path.Combine(stagingRoot, "journal.json"), manifest, cancellationToken);

        var oldFiles = ValidPrevious(previous, manifest)
            ? previous!.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal)
            : new Dictionary<string, IncrementalFileManifest>(StringComparer.Ordinal);

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = EnsureWithin(currentRoot, Path.Combine(currentRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            oldFiles.TryGetValue(file.RelativePath, out var oldFile);
            if (File.Exists(destination) && await FileMatchesAsync(destination, file.ContentHash, cancellationToken)) continue;
            if (oldFile is null && File.Exists(destination))
                throw new InvalidDataException($"Das Plain-Current-Ziel enthält eine nicht katalogisierte Datei: {file.RelativePath}");
            if (oldFile is not null && !File.Exists(destination))
                throw new InvalidDataException($"Die katalogisierte Current-Datei fehlt: {file.RelativePath}");

            await using var oldInput = oldFile is null
                ? null
                : new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (oldInput is not null)
            {
                var oldHash = await HashStreamAsync(oldInput, cancellationToken);
                if (!oldHash.Equals(oldFile!.ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Die Current-Datei '{file.RelativePath}' wurde außerhalb von MatBu verändert.");
                await PreserveChangedOldChunksLocalAsync(oldInput, oldFile, file, chunkRoot, cancellationToken);
            }

            var staged = EnsureWithin(stagingRoot, Path.Combine(stagingRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar) + ".partial"));
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            await BuildFileAsync(staged, file, oldFile, oldInput, job.TransferId, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(staged, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, file.LastWriteDate.UtcDateTime);
        }

        var newPaths = manifest.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var oldFile in oldFiles.Values.Where(file => !newPaths.Contains(file.RelativePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = EnsureWithin(currentRoot, Path.Combine(currentRoot, oldFile.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(path)) continue;
            await using var oldInput = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            var oldHash = await HashStreamAsync(oldInput, cancellationToken);
            if (!oldHash.Equals(oldFile.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Die zu löschende Current-Datei '{oldFile.RelativePath}' wurde außerhalb von MatBu verändert.");
            await PreserveAllOldChunksLocalAsync(oldInput, oldFile, chunkRoot, cancellationToken);
            File.Delete(path);
        }

        await IncrementalManifestJson.WriteAsync(manifestPath, manifest, cancellationToken);
        TryDeleteDirectory(stagingRoot);
        logger.LogInformation("Reverse incremental snapshot {SnapshotToken} published to {Destination}", manifest.SnapshotToken, currentRoot);
        return new IncrementalApplyResult(currentRoot, manifest.TotalBytes, manifest.StoredBytes, manifest.ReusedBytes, manifest.Files.Count, manifestPath);
    }

    private async Task MaterializeLocalSnapshotAsync(
        BackupTask task,
        BackupObject target,
        IncrementalBackupManifest selected,
        IncrementalBackupManifest latest,
        string treeRoot,
        CancellationToken cancellationToken)
    {
        var targetRoot = Path.GetFullPath(target.Location);
        var repositoryBase = EnsureWithin(targetRoot, Path.Combine(targetRoot, RepositoryRelativeBase(task).Replace('/', Path.DirectorySeparatorChar)));
        var currentRoot = Path.Combine(repositoryBase, "current");
        var chunkRoot = Path.Combine(repositoryBase, ".matbu", "chunks");
        var latestFiles = latest.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var file in selected.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latestFiles.TryGetValue(file.RelativePath, out var latestFile);
            var currentPath = EnsureWithin(currentRoot, Path.Combine(currentRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var destination = EnsureWithin(treeRoot, Path.Combine(treeRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var current = File.Exists(currentPath)
                ? new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess)
                : null;
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            foreach (var chunk in file.Chunks.OrderBy(item => item.Sequence))
            {
                var stored = Path.Combine(chunkRoot, chunk.Hash[..2], chunk.Hash + ".chunk");
                if (File.Exists(stored))
                {
                    await using var input = new FileStream(stored, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await CopyExactAndVerifyAsync(input, output, chunk.Length, chunk.Hash, cancellationToken);
                    continue;
                }
                var currentChunk = latestFile?.Chunks.FirstOrDefault(item => item.Sequence == chunk.Sequence && item.Hash.Equals(chunk.Hash, StringComparison.OrdinalIgnoreCase));
                if (current is null || currentChunk is null)
                    throw new InvalidDataException($"Snapshot-Chunk {chunk.Hash} für '{file.RelativePath}' ist weder als Reverse-Delta noch in Plain Current verfügbar.");
                current.Position = currentChunk.Offset;
                await CopyExactAndVerifyAsync(current, output, chunk.Length, chunk.Hash, cancellationToken);
            }
            File.SetLastWriteTimeUtc(destination, file.LastWriteDate.UtcDateTime);
        }
    }

    private async Task MaterializeSmbSnapshotAsync(
        BackupTask task,
        BackupObject target,
        IncrementalBackupManifest selected,
        IncrementalBackupManifest latest,
        string treeRoot,
        string workingRoot,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var relativeBase = RepositoryRelativeBase(task);
        var currentBase = $"{relativeBase}/current";
        var metadataBase = $"{relativeBase}/.matbu";
        var latestFiles = latest.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var file in selected.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latestFiles.TryGetValue(file.RelativePath, out var latestFile);
            var chunkAvailability = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var hash in file.Chunks.Select(chunk => chunk.Hash).Distinct(StringComparer.OrdinalIgnoreCase))
                chunkAvailability[hash] = await smbClient.RelativeFileExistsAsync(target.Location, $"{metadataBase}/chunks/{hash[..2]}/{hash}.chunk", credential, cancellationToken);

            FileStream? current = null;
            var needsCurrent = file.Chunks.Any(chunk => !chunkAvailability[chunk.Hash]);
            if (needsCurrent)
            {
                if (latestFile is null) throw new InvalidDataException($"Plain-Current-Manifest für '{file.RelativePath}' fehlt.");
                var localCurrent = Path.Combine(workingRoot, "current", file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(localCurrent)!);
                await smbClient.DownloadRelativeFileAsync(target.Location, $"{currentBase}/{file.RelativePath}", localCurrent, credential, cancellationToken);
                current = new FileStream(localCurrent, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            }
            try
            {
                var destination = EnsureWithin(treeRoot, Path.Combine(treeRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                foreach (var chunk in file.Chunks.OrderBy(item => item.Sequence))
                {
                    if (chunkAvailability[chunk.Hash])
                    {
                        var localChunk = Path.Combine(workingRoot, "chunks", chunk.Hash + ".chunk");
                        if (!File.Exists(localChunk))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(localChunk)!);
                            await smbClient.DownloadRelativeFileAsync(target.Location, $"{metadataBase}/chunks/{chunk.Hash[..2]}/{chunk.Hash}.chunk", localChunk, credential, cancellationToken);
                        }
                        await using var input = new FileStream(localChunk, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await CopyExactAndVerifyAsync(input, output, chunk.Length, chunk.Hash, cancellationToken);
                        continue;
                    }
                    var currentChunk = latestFile?.Chunks.FirstOrDefault(item => item.Sequence == chunk.Sequence && item.Hash.Equals(chunk.Hash, StringComparison.OrdinalIgnoreCase));
                    if (current is null || currentChunk is null)
                        throw new InvalidDataException($"Snapshot-Chunk {chunk.Hash} für '{file.RelativePath}' ist auf dem SMB-Ziel nicht verfügbar.");
                    current.Position = currentChunk.Offset;
                    await CopyExactAndVerifyAsync(current, output, chunk.Length, chunk.Hash, cancellationToken);
                }
                File.SetLastWriteTimeUtc(destination, file.LastWriteDate.UtcDateTime);
            }
            finally
            {
                if (current is not null) await current.DisposeAsync();
            }
        }
    }

    private async Task<IncrementalApplyResult> ApplySmbAsync(
        BackupTask task,
        BackupObject target,
        TransferJob job,
        IncrementalBackupManifest manifest,
        IncrementalBackupManifest? previous,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var relativeBase = RepositoryRelativeBase(task);
        var currentBase = $"{relativeBase}/current";
        var metadataBase = $"{relativeBase}/.matbu";
        var stagingRoot = Path.Combine(_dataPath, "transfer-cache", "incremental-target", job.TransferId);
        Directory.CreateDirectory(stagingRoot);
        await smbClient.EnsureDirectoryAsync(target.Location, currentBase, credential, cancellationToken);
        await smbClient.EnsureDirectoryAsync(target.Location, $"{metadataBase}/chunks", credential, cancellationToken);
        await smbClient.EnsureDirectoryAsync(target.Location, $"{metadataBase}/manifests", credential, cancellationToken);

        var journal = Path.Combine(stagingRoot, "journal.json");
        await WriteJournalAsync(journal, manifest, cancellationToken);
        await smbClient.UploadRelativeFileAsync(target.Location, journal, $"{metadataBase}/staging/{job.TransferId}/journal.json", credential, cancellationToken);

        var oldFiles = ValidPrevious(previous, manifest)
            ? previous!.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal)
            : new Dictionary<string, IncrementalFileManifest>(StringComparer.Ordinal);

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            oldFiles.TryGetValue(file.RelativePath, out var oldFile);
            var remoteCurrent = $"{currentBase}/{file.RelativePath}";
            var oldLocal = Path.Combine(stagingRoot, "old", file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            FileStream? oldInput = null;
            try
            {
                if (oldFile is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(oldLocal)!);
                    await smbClient.DownloadRelativeFileAsync(target.Location, remoteCurrent, oldLocal, credential, cancellationToken);
                    if (await FileMatchesAsync(oldLocal, file.ContentHash, cancellationToken)) continue;
                    if (!await FileMatchesAsync(oldLocal, oldFile.ContentHash, cancellationToken))
                        throw new InvalidDataException($"Die SMB-Current-Datei '{file.RelativePath}' wurde außerhalb von MatBu verändert.");
                    oldInput = new FileStream(oldLocal, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
                    await PreserveChangedOldChunksSmbAsync(oldInput, oldFile, file, target.Location, metadataBase, stagingRoot, credential, cancellationToken);
                }
                else if (await smbClient.RelativeFileExistsAsync(target.Location, remoteCurrent, credential, cancellationToken))
                {
                    throw new InvalidDataException($"Das SMB-Plain-Current-Ziel enthält eine nicht katalogisierte Datei: {file.RelativePath}");
                }

                var staged = Path.Combine(stagingRoot, "new", file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await BuildFileAsync(staged, file, oldFile, oldInput, job.TransferId, cancellationToken);
                await smbClient.UploadRelativeFileAsync(target.Location, staged, remoteCurrent, credential, cancellationToken);
            }
            finally
            {
                if (oldInput is not null) await oldInput.DisposeAsync();
            }
        }

        var newPaths = manifest.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var oldFile in oldFiles.Values.Where(file => !newPaths.Contains(file.RelativePath)))
        {
            var remoteCurrent = $"{currentBase}/{oldFile.RelativePath}";
            if (!await smbClient.RelativeFileExistsAsync(target.Location, remoteCurrent, credential, cancellationToken)) continue;
            var oldLocal = Path.Combine(stagingRoot, "deleted", oldFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(oldLocal)!);
            await smbClient.DownloadRelativeFileAsync(target.Location, remoteCurrent, oldLocal, credential, cancellationToken);
            if (!await FileMatchesAsync(oldLocal, oldFile.ContentHash, cancellationToken))
                throw new InvalidDataException($"Die zu löschende SMB-Current-Datei '{oldFile.RelativePath}' wurde außerhalb von MatBu verändert.");
            await using (var input = new FileStream(oldLocal, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess))
                await PreserveAllOldChunksSmbAsync(input, oldFile, target.Location, metadataBase, stagingRoot, credential, cancellationToken);
            await smbClient.DeleteRelativeFileAsync(target.Location, remoteCurrent, credential, cancellationToken);
        }

        var localManifest = Path.Combine(stagingRoot, manifest.SnapshotToken + ".json");
        await IncrementalManifestJson.WriteAsync(localManifest, manifest, cancellationToken);
        var remoteManifest = $"{metadataBase}/manifests/{manifest.SnapshotToken}.json";
        await smbClient.UploadRelativeFileAsync(target.Location, localManifest, remoteManifest, credential, cancellationToken);
        await smbClient.DeleteRelativeFileAsync(target.Location, $"{metadataBase}/staging/{job.TransferId}/journal.json", credential, cancellationToken);
        TryDeleteDirectory(stagingRoot);
        var destination = target.Location.TrimEnd('\\', '/') + "\\" + currentBase.Replace('/', '\\');
        var manifestDestination = target.Location.TrimEnd('\\', '/') + "\\" + remoteManifest.Replace('/', '\\');
        logger.LogInformation("Reverse incremental snapshot {SnapshotToken} published to {Destination}", manifest.SnapshotToken, destination);
        return new IncrementalApplyResult(destination, manifest.TotalBytes, manifest.StoredBytes, manifest.ReusedBytes, manifest.Files.Count, manifestDestination);
    }

    private async Task BuildFileAsync(
        string outputPath,
        IncrementalFileManifest file,
        IncrementalFileManifest? oldFile,
        FileStream? oldInput,
        string transferId,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (var chunk in file.Chunks.OrderBy(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Changed)
            {
                var sourcePath = sources.ChunkPath(transferId, chunk.Hash);
                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await CopyExactAndVerifyAsync(input, output, chunk.Length, chunk.Hash, cancellationToken);
                continue;
            }

            var oldChunk = oldFile?.Chunks.FirstOrDefault(candidate => candidate.Sequence == chunk.Sequence && candidate.Hash.Equals(chunk.Hash, StringComparison.OrdinalIgnoreCase));
            if (oldInput is null || oldChunk is null)
                throw new InvalidDataException($"Unveränderter Chunk {chunk.Hash} kann nicht aus Plain Current gelesen werden.");
            oldInput.Position = oldChunk.Offset;
            await CopyExactAndVerifyAsync(oldInput, output, oldChunk.Length, oldChunk.Hash, cancellationToken);
        }
        if (output.Length != file.Length) throw new InvalidDataException($"Die rekonstruierte Datei '{file.RelativePath}' hat eine falsche Länge.");
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        output.Position = 0;
        var hash = await HashStreamAsync(output, cancellationToken);
        if (!hash.Equals(file.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"End-to-End-Prüfsumme für '{file.RelativePath}' stimmt nicht.");
    }

    private async Task PreserveChangedOldChunksLocalAsync(FileStream oldInput, IncrementalFileManifest oldFile, IncrementalFileManifest newFile, string chunkRoot, CancellationToken cancellationToken)
    {
        var newBySequence = newFile.Chunks.ToDictionary(chunk => chunk.Sequence);
        foreach (var oldChunk in oldFile.Chunks.Where(chunk => !newBySequence.TryGetValue(chunk.Sequence, out var next) || !next.Hash.Equals(chunk.Hash, StringComparison.OrdinalIgnoreCase)))
            await PreserveChunkLocalAsync(oldInput, oldChunk, chunkRoot, cancellationToken);
    }

    private async Task PreserveAllOldChunksLocalAsync(FileStream oldInput, IncrementalFileManifest oldFile, string chunkRoot, CancellationToken cancellationToken)
    {
        foreach (var chunk in oldFile.Chunks) await PreserveChunkLocalAsync(oldInput, chunk, chunkRoot, cancellationToken);
    }

    private static async Task PreserveChunkLocalAsync(FileStream oldInput, IncrementalChunkManifest chunk, string chunkRoot, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(chunkRoot, chunk.Hash[..2], chunk.Hash + ".chunk");
        if (File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".partial";
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            oldInput.Position = chunk.Offset;
            await CopyExactAndVerifyAsync(oldInput, output, chunk.Length, chunk.Hash, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        File.Move(partial, destination, overwrite: false);
    }

    private async Task PreserveChangedOldChunksSmbAsync(FileStream oldInput, IncrementalFileManifest oldFile, IncrementalFileManifest newFile, string location, string metadataBase, string stagingRoot, (string Username, string Password)? credential, CancellationToken cancellationToken)
    {
        var newBySequence = newFile.Chunks.ToDictionary(chunk => chunk.Sequence);
        foreach (var oldChunk in oldFile.Chunks.Where(chunk => !newBySequence.TryGetValue(chunk.Sequence, out var next) || !next.Hash.Equals(chunk.Hash, StringComparison.OrdinalIgnoreCase)))
            await PreserveChunkSmbAsync(oldInput, oldChunk, location, metadataBase, stagingRoot, credential, cancellationToken);
    }

    private async Task PreserveAllOldChunksSmbAsync(FileStream oldInput, IncrementalFileManifest oldFile, string location, string metadataBase, string stagingRoot, (string Username, string Password)? credential, CancellationToken cancellationToken)
    {
        foreach (var chunk in oldFile.Chunks)
            await PreserveChunkSmbAsync(oldInput, chunk, location, metadataBase, stagingRoot, credential, cancellationToken);
    }

    private async Task PreserveChunkSmbAsync(FileStream oldInput, IncrementalChunkManifest chunk, string location, string metadataBase, string stagingRoot, (string Username, string Password)? credential, CancellationToken cancellationToken)
    {
        var relative = $"{metadataBase}/chunks/{chunk.Hash[..2]}/{chunk.Hash}.chunk";
        if (await smbClient.RelativeFileExistsAsync(location, relative, credential, cancellationToken)) return;
        var local = Path.Combine(stagingRoot, "reverse-chunks", chunk.Hash + ".chunk");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await using (var output = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            oldInput.Position = chunk.Offset;
            await CopyExactAndVerifyAsync(oldInput, output, chunk.Length, chunk.Hash, cancellationToken);
        }
        await smbClient.UploadRelativeFileAsync(location, local, relative, credential, cancellationToken, skipIfExists: true);
        TryDelete(local);
    }

    private void RecordSnapshot(BackupTask task, TransferJob job, IncrementalBackupManifest manifest, IncrementalApplyResult result)
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
                Token = manifest.SnapshotToken,
                Method = task.Method,
                State = "Completed",
                RootPath = result.Destination,
                ManifestPath = result.RepositoryManifestPath,
                FileCount = result.FileCount,
                TotalBytes = result.TotalBytes,
                StoredBytes = result.StoredBytes,
                ReusedBytes = result.ReusedBytes,
                CreateDate = now,
                UpdateDate = now
            });
            var currentJob = data.TransferJobs.FirstOrDefault(item => item.Id == job.Id);
            if (currentJob is null) return;
            currentJob.Method = task.Method;
            currentJob.SnapshotId = snapshotId;
            currentJob.SourceBytes = result.TotalBytes;
            currentJob.StoredBytes = result.StoredBytes;
            currentJob.ReusedBytes = result.ReusedBytes;
            currentJob.UpdateDate = now;
        });
    }

    public async Task<int> ApplyRetentionAsync(
        BackupTask task,
        BackupObject target,
        IReadOnlyList<string> expiredSnapshotTokens,
        IReadOnlyList<string> retainedSnapshotTokens,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var expiredManifests = await LoadCatalogManifestsAsync(task.Token, expiredSnapshotTokens, cancellationToken);
        var retainedManifests = await LoadCatalogManifestsAsync(task.Token, retainedSnapshotTokens, cancellationToken);
        var latest = await LoadPreviousManifestAsync(task.Token, cancellationToken);
        if (latest is not null) retainedManifests.Add(latest);

        var retainedHashes = retainedManifests
            .SelectMany(manifest => manifest.Files)
            .SelectMany(file => file.Chunks)
            .Select(chunk => chunk.Hash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletableHashes = expiredManifests
            .SelectMany(manifest => manifest.Files)
            .SelectMany(file => file.Chunks)
            .Select(chunk => chunk.Hash)
            .Where(hash => !retainedHashes.Contains(hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (target.Kind == ObjectKind.LocalFolder)
        {
            var root = Path.GetFullPath(target.Location);
            var repositoryBase = EnsureWithin(root, Path.Combine(root, RepositoryRelativeBase(task).Replace('/', Path.DirectorySeparatorChar)));
            var metadataRoot = Path.Combine(repositoryBase, ".matbu");
            foreach (var token in expiredSnapshotTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(EnsureWithin(metadataRoot, Path.Combine(metadataRoot, "manifests", SafeToken(token) + ".json")));
            }
            foreach (var hash in deletableHashes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(EnsureWithin(metadataRoot, Path.Combine(metadataRoot, "chunks", hash[..2], hash + ".chunk")));
            }
        }
        else if (target.Kind == ObjectKind.Smb)
        {
            var metadataBase = $"{RepositoryRelativeBase(task)}/.matbu";
            foreach (var token in expiredSnapshotTokens)
                await smbClient.DeleteRelativeFileAsync(target.Location, $"{metadataBase}/manifests/{SafeToken(token)}.json", credential, cancellationToken);
            foreach (var hash in deletableHashes)
                await smbClient.DeleteRelativeFileAsync(target.Location, $"{metadataBase}/chunks/{hash[..2]}/{hash}.chunk", credential, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException($"Reverse-Incremental-Retention unterstützt das Ziel '{target.Kind}' noch nicht.");
        }

        await DeleteCatalogSnapshotsAsync(task.Token, expiredSnapshotTokens, cancellationToken);
        return deletableHashes.Count;
    }

    public Task DeleteCatalogSnapshotsAsync(string taskToken, IEnumerable<string> snapshotTokens, CancellationToken cancellationToken)
    {
        foreach (var token in snapshotTokens.Where(token => !string.IsNullOrWhiteSpace(token)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDelete(CatalogSnapshotPath(taskToken, token));
        }
        return Task.CompletedTask;
    }

    private async Task<List<IncrementalBackupManifest>> LoadCatalogManifestsAsync(
        string taskToken,
        IEnumerable<string> snapshotTokens,
        CancellationToken cancellationToken)
    {
        var result = new List<IncrementalBackupManifest>();
        foreach (var token in snapshotTokens.Where(token => !string.IsNullOrWhiteSpace(token)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = CatalogSnapshotPath(taskToken, token);
            if (File.Exists(path)) result.Add(await IncrementalManifestJson.ReadAsync(path, cancellationToken));
        }
        return result;
    }

    private async Task SaveCatalogAsync(string taskToken, IncrementalBackupManifest manifest, CancellationToken cancellationToken)
    {
        var snapshotPath = CatalogSnapshotPath(taskToken, manifest.SnapshotToken);
        await IncrementalManifestJson.WriteAsync(snapshotPath, manifest, cancellationToken);
        await IncrementalManifestJson.WriteAsync(CatalogLatestPath(taskToken), manifest, cancellationToken);
        var baselinePath = CatalogBaselinePath(taskToken);
        if (!File.Exists(baselinePath)) await IncrementalManifestJson.WriteAsync(baselinePath, manifest, cancellationToken);
    }

    private string CatalogLatestPath(string taskToken) => Path.Combine(CatalogRoot(taskToken), "latest.json");
    private string CatalogBaselinePath(string taskToken) => Path.Combine(CatalogRoot(taskToken), "baseline.json");
    private string CatalogSnapshotPath(string taskToken, string snapshotToken) => Path.Combine(CatalogRoot(taskToken), "snapshots", snapshotToken + ".json");
    private string CatalogRoot(string taskToken) => Path.Combine(_dataPath, "repository-catalog", SafeToken(taskToken));

    private static bool ValidPrevious(IncrementalBackupManifest? previous, IncrementalBackupManifest current) =>
        previous is not null &&
        previous.RepositoryKey.Equals(current.RepositoryKey, StringComparison.Ordinal) &&
        previous.ChunkSizeBytes == current.ChunkSizeBytes;

    private static async Task WriteJournalAsync(string path, IncrementalBackupManifest manifest, CancellationToken cancellationToken) =>
        await IncrementalManifestJson.WriteAsync(path, manifest, cancellationToken);

    private static async Task<bool> FileMatchesAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await HashStreamAsync(input, cancellationToken);
        return actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> HashStreamAsync(Stream input, CancellationToken cancellationToken)
    {
        input.Position = 0;
        var hash = await SHA256.HashDataAsync(input, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task CopyExactAndVerifyAsync(Stream input, Stream output, int length, string expectedHash, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(1024 * 1024, Math.Max(1, length)));
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("Ein Incremental-Chunk endet vor der erwarteten Länge.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Chunk-Prüfsumme stimmt nicht: erwartet {expectedHash}, gelesen {actual}.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string EnsureWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.Equals(fullRoot, comparison) && !fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Ein Repository-Pfad verlässt das konfigurierte Ziel.");
        return fullCandidate;
    }

    private static string SafeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException("Ungültiges Job-Token für den Repository-Katalog.");
        return value;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
}
