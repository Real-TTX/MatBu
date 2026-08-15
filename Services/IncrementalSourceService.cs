using System.Buffers;
using System.Formats.Tar;
using System.Security.Cryptography;
using MatBu.Models;

namespace MatBu.Services;

public sealed class IncrementalSourceService(
    IHostEnvironment environment,
    ArchiveService archiveService,
    ILogger<IncrementalSourceService> logger)
{
    private readonly string _dataPath = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? Path.Combine(environment.ContentRootPath, "data");

    public string TransferDirectory(string transferId)
    {
        EnsureTransferId(transferId);
        return Path.Combine(_dataPath, "transfer-cache", "incremental", transferId);
    }

    public string ManifestPath(string transferId) => Path.Combine(TransferDirectory(transferId), "manifest.json");
    public string ChunkDirectory(string transferId) => Path.Combine(TransferDirectory(transferId), "chunks");

    public string ChunkPath(string transferId, string hash)
    {
        EnsureHash(hash);
        return Path.Combine(ChunkDirectory(transferId), hash[..2], hash + ".chunk");
    }

    public async Task<IncrementalSourcePreparation> PrepareAsync(
        BackupObject source,
        (string Username, string Password)? credential,
        string taskToken,
        string transferId,
        int chunkSizeMiB,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? includedPaths = null)
    {
        var chunkSize = ValidateChunkSize(chunkSizeMiB) * 1024 * 1024;
        var workingDirectory = TransferDirectory(transferId);
        var manifestPath = ManifestPath(transferId);
        var chunks = ChunkDirectory(transferId);
        Directory.CreateDirectory(chunks);

        if (File.Exists(manifestPath))
        {
            var cached = await IncrementalManifestJson.ReadAsync(manifestPath, cancellationToken);
            if (cached.ChunkSizeBytes == chunkSize && cached.TaskToken == taskToken && AllChunksPresent(cached, transferId))
                return new IncrementalSourcePreparation(cached, workingDirectory, chunks, manifestPath);
        }

        ResetWorkingDirectory(workingDirectory);
        Directory.CreateDirectory(chunks);
        var manifest = new IncrementalBackupManifest
        {
            TaskToken = taskToken,
            TransferId = transferId,
            ChunkSizeBytes = chunkSize,
            Method = BackupMethod.ReverseIncremental,
            CreateDate = DateTimeOffset.UtcNow
        };

        if (source.Kind == ObjectKind.LocalFolder)
        {
            await ScanLocalFolderAsync(source.Location, manifest, transferId, SourceSelection.Normalize(includedPaths ?? []), cancellationToken);
        }
        else if (source.Kind is ObjectKind.Smb or ObjectKind.DockerVolume or ObjectKind.Proxmox)
        {
            var archive = Path.Combine(workingDirectory, "source.tar");
            await archiveService.CreateCompressedAsync(source, credential, archive, BackupCompression.None, null, cancellationToken, includedPaths);
            try { await ScanTarAsync(archive, manifest, transferId, SourceSelection.Normalize(includedPaths ?? []), cancellationToken); }
            finally { TryDelete(archive); }
        }
        else
        {
            throw new InvalidOperationException($"Reverse Incremental unterstützt die Quelle '{source.Kind}' noch nicht.");
        }

        manifest.Files = manifest.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
        await IncrementalManifestJson.WriteAsync(manifestPath, manifest, cancellationToken);
        logger.LogInformation(
            "Prepared incremental source {TransferId}: {Files} files, {Bytes} bytes, {Chunks} chunks",
            transferId,
            manifest.Files.Count,
            manifest.TotalBytes,
            manifest.Files.Sum(file => file.Chunks.Count));
        return new IncrementalSourcePreparation(manifest, workingDirectory, chunks, manifestPath);
    }

    public void ApplyPreviousManifest(IncrementalBackupManifest manifest, IncrementalBackupManifest? previous, string repositoryKey)
    {
        manifest.RepositoryKey = repositoryKey;
        var canReuse = previous is not null &&
            previous.FormatVersion == manifest.FormatVersion &&
            previous.ChunkSizeBytes == manifest.ChunkSizeBytes &&
            previous.RepositoryKey.Equals(repositoryKey, StringComparison.Ordinal);
        var oldFiles = canReuse
            ? previous!.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal)
            : new Dictionary<string, IncrementalFileManifest>(StringComparer.Ordinal);

        foreach (var file in manifest.Files)
        {
            oldFiles.TryGetValue(file.RelativePath, out var oldFile);
            foreach (var chunk in file.Chunks)
            {
                var oldChunk = oldFile?.Chunks.FirstOrDefault(candidate => candidate.Sequence == chunk.Sequence);
                chunk.Changed = oldChunk is null ||
                    oldChunk.Offset != chunk.Offset ||
                    oldChunk.Length != chunk.Length ||
                    !oldChunk.Hash.Equals(chunk.Hash, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    public static void MarkChunksNeededForTransition(IncrementalBackupManifest manifest, IncrementalBackupManifest? previous)
    {
        if (previous is null || previous.ChunkSizeBytes != manifest.ChunkSizeBytes) return;
        var oldFiles = previous.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            oldFiles.TryGetValue(file.RelativePath, out var oldFile);
            foreach (var chunk in file.Chunks.Where(chunk => !chunk.Changed))
            {
                var oldChunk = oldFile?.Chunks.FirstOrDefault(candidate => candidate.Sequence == chunk.Sequence);
                if (oldChunk is null || oldChunk.Offset != chunk.Offset || oldChunk.Length != chunk.Length ||
                    !oldChunk.Hash.Equals(chunk.Hash, StringComparison.OrdinalIgnoreCase))
                    chunk.Changed = true;
            }
        }
    }

    public IReadOnlyList<string> FindMissingChangedHashes(IncrementalBackupManifest manifest, string transferId)
    {
        return manifest.Files
            .SelectMany(file => file.Chunks)
            .Where(chunk => chunk.Changed)
            .Select(chunk => chunk.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(hash => !IsValidChunkFile(ChunkPath(transferId, hash), hash))
            .ToList();
    }

    public async Task<long> ReceiveChunkAsync(string transferId, string hash, Stream body, CancellationToken cancellationToken)
    {
        EnsureTransferId(transferId);
        EnsureHash(hash);
        var final = ChunkPath(transferId, hash);
        if (IsValidChunkFile(final, hash)) return new FileInfo(final).Length;
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var partial = final + ".partial";
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await body.CopyToAsync(output, cancellationToken);
        var actual = await HashFileAsync(partial, cancellationToken);
        if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partial);
            throw new InvalidDataException($"Chunk-Prüfsumme stimmt nicht. Erwartet {hash}, empfangen {actual}.");
        }
        File.Move(partial, final, overwrite: true);
        return new FileInfo(final).Length;
    }

    public async Task VerifyChangedChunksAsync(IncrementalBackupManifest manifest, string transferId, CancellationToken cancellationToken)
    {
        foreach (var hash in manifest.Files.SelectMany(file => file.Chunks).Where(chunk => chunk.Changed).Select(chunk => chunk.Hash).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ChunkPath(transferId, hash);
            if (!File.Exists(path)) throw new FileNotFoundException($"Der benötigte Incremental-Chunk {hash} fehlt.", path);
            var actual = await HashFileAsync(path, cancellationToken);
            if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Der Incremental-Chunk {hash} ist beschädigt.");
        }
    }

    private async Task ScanLocalFolderAsync(string rootPath, IncrementalBackupManifest manifest, string transferId, IReadOnlyList<string> selection, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException($"Quellordner wurde nicht gefunden: {rootPath}");
        var root = Path.GetFullPath(rootPath);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", options).OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!SourceSelection.Includes(relative, selection)) continue;
            if (!RestoreArchiveService.TryNormalizeEntryPath(relative, out var normalized))
                throw new InvalidDataException($"Ungültiger Quellpfad: {relative}");
            if (normalized.StartsWith(".matbu/", StringComparison.OrdinalIgnoreCase) || normalized.Equals(".matbu", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest.Files.Add(await ScanFileAsync(normalized, info.LastWriteTimeUtc, input, manifest.ChunkSizeBytes, transferId, cancellationToken));
        }
    }

    private async Task ScanTarAsync(string archivePath, IncrementalBackupManifest manifest, string transferId, IReadOnlyList<string> selection, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new TarReader(input, leaveOpen: false);
        TarEntry? entry;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null) continue;
            if (!RestoreArchiveService.TryNormalizeEntryPath(entry.Name, out var normalized)) continue;
            if (!SourceSelection.Includes(normalized, selection)) continue;
            if (!seen.Add(normalized)) throw new InvalidDataException($"Die Quelle enthält den Dateipfad '{normalized}' mehrfach.");
            manifest.Files.Add(await ScanFileAsync(normalized, entry.ModificationTime, entry.DataStream, manifest.ChunkSizeBytes, transferId, cancellationToken));
        }
    }

    private async Task<IncrementalFileManifest> ScanFileAsync(
        string relativePath,
        DateTimeOffset lastWriteDate,
        Stream input,
        int chunkSize,
        string transferId,
        CancellationToken cancellationToken)
    {
        var file = new IncrementalFileManifest { RelativePath = relativePath, LastWriteDate = lastWriteDate };
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            long offset = 0;
            var sequence = 0;
            while (true)
            {
                var read = await ReadChunkAsync(input, buffer.AsMemory(0, chunkSize), cancellationToken);
                if (read == 0) break;
                fileHash.AppendData(buffer, 0, read);
                var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read))).ToLowerInvariant();
                var chunkPath = ChunkPath(transferId, hash);
                if (!File.Exists(chunkPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(chunkPath)!);
                    var partial = chunkPath + ".partial";
                    await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    File.Move(partial, chunkPath, overwrite: true);
                }
                file.Chunks.Add(new IncrementalChunkManifest { Sequence = sequence++, Offset = offset, Length = read, Hash = hash, Changed = true });
                offset += read;
            }
            file.Length = offset;
            file.ContentHash = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
            return file;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<int> ReadChunkAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private bool AllChunksPresent(IncrementalBackupManifest manifest, string transferId) => manifest.Files
        .SelectMany(file => file.Chunks)
        .Select(chunk => chunk.Hash)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .All(hash => IsValidChunkFile(ChunkPath(transferId, hash), hash));

    private static bool IsValidChunkFile(string path, string hash)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var input = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            return actual.Equals(hash, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
    }

    private static int ValidateChunkSize(int value) => value is 4 or 8 or 16 or 32
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value), "Die Chunkgröße muss 4, 8, 16 oder 32 MiB betragen.");

    private static void EnsureTransferId(string transferId)
    {
        if (!Guid.TryParse(transferId, out _)) throw new ArgumentException("Ungültige Transfer-ID.", nameof(transferId));
    }

    private static void EnsureHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Ungültiger SHA-256-Chunk-Hash.", nameof(hash));
    }

    private static void ResetWorkingDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
