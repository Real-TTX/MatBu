using System.Net.Http.Json;
using System.Diagnostics;
using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed record ArchiveProgress(long SourceBytes, long StoredBytes, long EstimatedSourceBytes, long EstimatedStoredBytes, long SpeedBytesPerSecond);
public sealed record ArchiveCreationResult(long SourceBytes, long StoredBytes, string Sha256 = "");

public sealed class ArchiveService(IHostEnvironment environment, SmbClientService smbClient, ProxmoxService proxmox, ILogger<ArchiveService> logger, TransferSettingsStore? transferSettings = null)
{
    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;
    private const long FreeSpaceCheckInterval = 64L * MiB;
    private readonly string _dataPath = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? Path.Combine(environment.ContentRootPath, "data");

    public string CacheDirectory
    {
        get
        {
            var directory = Path.Combine(_dataPath, "transfer-cache");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public async Task CreateAsync(BackupObject source, (string Username, string Password)? credential, string outputPath, CancellationToken cancellationToken)
    {
        _ = await CreateCompressedAsync(source, credential, outputPath, BackupCompression.None, null, cancellationToken);
    }

    public async Task<ArchiveCreationResult> CreateCompressedAsync(
        BackupObject source,
        (string Username, string Password)? credential,
        string outputPath,
        BackupCompression compression,
        Action<ArchiveProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? includedPaths = null,
        Action<long>? throttle = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var selection = SourceSelection.Normalize(includedPaths ?? []);
        // Determine the total source size CONCURRENTLY instead of blocking the first produced byte on it:
        // on a large/slow tree the recursive size scan can take longer than the secondary idle watchdog,
        // which would stall the transfer before any data flows. The estimate snaps in once it resolves.
        long estimatedSourceBytes = 0;
        using var estimateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? estimateTask = source.Kind == ObjectKind.LocalFolder
            ? Task.Run(async () =>
            {
                try { Interlocked.Exchange(ref estimatedSourceBytes, await EstimateLocalTarBytesAsync(source.Location, selection, estimateCts.Token)); }
                catch { /* best effort: total stays 0 (indeterminate) until/if it resolves */ }
            }, estimateCts.Token)
            : null;
        var stopwatch = Stopwatch.StartNew();
        long lastBytes = 0;
        long lastTicks = 0;
        long nextFreeSpaceCheck = 0;
        CountingWriteStream? storedCounter = null;
        CountingWriteStream? sourceCounter = null;
        void Report(bool force = false)
        {
            if (sourceCounter is null || storedCounter is null || progress is null) return;
            var elapsedTicks = stopwatch.ElapsedTicks;
            if (!force && elapsedTicks - lastTicks < Stopwatch.Frequency / 2) return;
            var elapsedSeconds = Math.Max(0.001, (double)(elapsedTicks - lastTicks) / Stopwatch.Frequency);
            var speed = (long)Math.Max(0, (sourceCounter.BytesWritten - lastBytes) / elapsedSeconds);
            var estimate = Interlocked.Read(ref estimatedSourceBytes);
            var estimatedStored = sourceCounter.BytesWritten > 0 && estimate > 0
                ? (long)Math.Ceiling(estimate * (double)storedCounter.BytesWritten / sourceCounter.BytesWritten)
                : 0;
            progress(new ArchiveProgress(sourceCounter.BytesWritten, storedCounter.BytesWritten, estimate, estimatedStored, speed));
            lastBytes = sourceCounter.BytesWritten;
            lastTicks = elapsedTicks;
        }

        var building = outputPath + ".building";
        // Docker streams a live volume. Its staging file must never be created in a
        // potentially identical mounted volume, otherwise the TAR can archive itself.
        var temporaryBuilding = source.Kind == ObjectKind.DockerVolume ||
                                source.Kind == ObjectKind.LocalFolder && IsPathWithin(source.Location, building)
                ? Path.Combine(Path.GetTempPath(), $"matbu-archive-{Guid.NewGuid():N}.tar.building")
                : building;
        var spoolDrive = ResolveDrive(temporaryBuilding);
        var minimumFreeSpace = ResolveMinimumFreeSpace(spoolDrive);
        void GuardFreeSpace(int pendingBytes)
        {
            var nextLength = (storedCounter?.BytesWritten ?? 0) + pendingBytes;
            if (nextLength < nextFreeSpaceCheck) return;
            spoolDrive = ResolveDrive(temporaryBuilding);
            if (spoolDrive.AvailableFreeSpace <= minimumFreeSpace + pendingBytes)
            {
                throw new IOException(
                    $"Transfer kontrolliert abgebrochen: Auf '{spoolDrive.Name}' sind nur noch {FormatBytes(spoolDrive.AvailableFreeSpace)} frei. " +
                    $"Die MatBu-Sicherheitsreserve von {FormatBytes(minimumFreeSpace)} wird nicht unterschritten.");
            }
            nextFreeSpaceCheck = nextLength + FreeSpaceCheckInterval;
        }
        void BeforeStoredWrite(int pendingBytes)
        {
            GuardFreeSpace(pendingBytes);
            throttle?.Invoke((storedCounter?.BytesWritten ?? 0) + pendingBytes);
        }
        using var storedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            if (File.Exists(temporaryBuilding)) File.Delete(temporaryBuilding);
            GuardFreeSpace(0);
            await using var file = new FileStream(temporaryBuilding, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            storedCounter = new CountingWriteStream(file, () => Report(), BeforeStoredWrite, storedHash);
            await using var compressor = CreateCompressionStream(storedCounter, compression);
            sourceCounter = new CountingWriteStream(compressor, () => Report());

            if (source.Kind == ObjectKind.LocalFolder)
            {
                if (!Directory.Exists(source.Location)) throw new DirectoryNotFoundException($"Quellordner wurde nicht gefunden: {source.Location}");
                await Task.Run(() => CreateLocalTar(source.Location, sourceCounter, selection, cancellationToken), cancellationToken);
            }
            else if (source.Kind == ObjectKind.Smb)
            {
                await smbClient.CreateArchiveAsync(source.Location, credential, sourceCounter, cancellationToken, selection);
            }
            else if (source.Kind == ObjectKind.DockerVolume)
            {
                await CreateDockerVolumeArchiveAsync(source.Location, sourceCounter, selection, cancellationToken);
            }
            else if (source.Kind == ObjectKind.Proxmox)
            {
                var backupFiles = await proxmox.CreateBackupFilesAsync(source.Location, credential?.Username, credential?.Password, selection, cancellationToken);
                try { await WriteFilesTarAsync(backupFiles, sourceCounter, cancellationToken); }
                finally { ProxmoxService.CleanupBackupFiles(backupFiles); }
            }
            else throw new InvalidOperationException($"Der Object-Typ {source.Kind} kann derzeit nicht als Quelle archiviert werden.");

            await sourceCounter.FlushAsync(cancellationToken);
            var sourceBytes = sourceCounter.BytesWritten;
            await sourceCounter.DisposeAsync();
            sourceCounter = null;
            await file.FlushAsync(cancellationToken);
            await file.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryBuilding, outputPath, overwrite: true);
            var storedBytes = new FileInfo(outputPath).Length;
            var sha256 = Convert.ToHexString(storedHash.GetHashAndReset()).ToLowerInvariant();
            // At completion the exact totals are known, so report them (resolves the UI to 100% even if
            // the background estimate had not finished yet).
            progress?.Invoke(new ArchiveProgress(sourceBytes, storedBytes, sourceBytes, storedBytes, 0));
            return new ArchiveCreationResult(sourceBytes, storedBytes, sha256);
        }
        finally
        {
            // Stop the background size estimate so it cannot keep scanning the tree after an early exit.
            estimateCts.Cancel();
            if (estimateTask is not null) { try { await estimateTask; } catch { } }
            if (sourceCounter is not null) await sourceCounter.DisposeAsync();
            try { if (File.Exists(temporaryBuilding)) File.Delete(temporaryBuilding); } catch { }
        }
    }

    private static async Task WriteFilesTarAsync(IReadOnlyList<string> files, Stream output, CancellationToken cancellationToken)
    {
        using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetFileName(path))
            {
                DataStream = input,
                ModificationTime = File.GetLastWriteTimeUtc(path)
            };
            await writer.WriteEntryAsync(entry, cancellationToken);
        }
    }

    public async Task ApplyRestoreArchiveAsync(ObjectKind targetKind, string targetLocation, string archivePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Das vorbereitete Restore-Archiv wurde nicht gefunden.", archivePath);

        if (targetKind == ObjectKind.LocalFolder)
        {
            Directory.CreateDirectory(targetLocation);
            await Task.Run(() => System.Formats.Tar.TarFile.ExtractToDirectory(archivePath, targetLocation, overwriteFiles: true), cancellationToken);
            return;
        }

        if (targetKind == ObjectKind.DockerVolume)
        {
            await ApplyDockerVolumeRestoreAsync(targetLocation, archivePath, cancellationToken);
            return;
        }

        throw new InvalidOperationException($"Der Object-Typ {targetKind} kann derzeit nicht als File-Level-Restore-Ziel verwendet werden.");
    }

    public async Task<IReadOnlyList<string>> BrowseDockerVolumeDirectoriesAsync(string volumeName, string? relativePath, CancellationToken cancellationToken)
    {
        var normalized = SourceSelection.Normalize(string.IsNullOrWhiteSpace(relativePath) ? [] : [relativePath]).FirstOrDefault() ?? "";
        using var client = CreateDockerClient();
        var volume = await client.GetAsync($"/v1.41/volumes/{Uri.EscapeDataString(volumeName)}", cancellationToken);
        if (!volume.IsSuccessStatusCode) throw new InvalidOperationException($"Docker-Volume '{volumeName}' wurde nicht gefunden.");
        var create = await client.PostAsJsonAsync("/v1.41/containers/create", new
        {
            Image = "busybox:latest",
            Cmd = new[] { "sleep", "3600" },
            Tty = true,
            HostConfig = new { AutoRemove = false, Mounts = new[] { new { Type = "volume", Source = volumeName, Target = "/source", ReadOnly = true } } }
        }, cancellationToken);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<ContainerResponse>(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(created?.Id)) throw new InvalidOperationException("Docker lieferte keine Container-ID.");
        try
        {
            (await client.PostAsync($"/v1.41/containers/{created.Id}/start", null, cancellationToken)).EnsureSuccessStatusCode();
            var directory = string.IsNullOrEmpty(normalized) ? "/source" : "/source/" + normalized;
            var execCreate = await client.PostAsJsonAsync($"/v1.41/containers/{created.Id}/exec", new
            {
                AttachStdout = true,
                AttachStderr = true,
                Tty = true,
                Cmd = new[] { "find", directory, "-mindepth", "1", "-maxdepth", "1", "-type", "d" }
            }, cancellationToken);
            execCreate.EnsureSuccessStatusCode();
            var exec = await execCreate.Content.ReadFromJsonAsync<ExecResponse>(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(exec?.Id)) throw new InvalidOperationException("Docker lieferte keine Exec-ID.");
            using var start = await client.PostAsJsonAsync($"/v1.41/exec/{exec.Id}/start", new { Detach = false, Tty = true }, cancellationToken);
            start.EnsureSuccessStatusCode();
            var output = await start.Content.ReadAsStringAsync(cancellationToken);
            using var inspect = await client.GetAsync($"/v1.41/exec/{exec.Id}/json", cancellationToken);
            inspect.EnsureSuccessStatusCode();
            var status = await inspect.Content.ReadFromJsonAsync<ExecInspectResponse>(cancellationToken: cancellationToken);
            if (status?.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(output) ? "Docker-Volume-Ordner konnten nicht gelesen werden." : output.Trim());
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => path.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            try { await client.DeleteAsync($"/v1.41/containers/{created.Id}?force=true", CancellationToken.None); } catch (Exception ex) { logger.LogDebug(ex, "Temporary Docker browse container cleanup failed"); }
        }
    }

    private Task CreateDockerVolumeArchiveAsync(string volumeName, Stream output, IReadOnlyList<string> selection, CancellationToken cancellationToken)
    {
        return ReadDockerVolumeArchiveAsync(volumeName, async input =>
        {
            using var reader = new System.Formats.Tar.TarReader(input, leaveOpen: true);
            using var writer = new System.Formats.Tar.TarWriter(output, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true);
            System.Formats.Tar.TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeDockerArchivePath(entry.Name);
                if (string.IsNullOrEmpty(relative) || selection.Count > 0 && !SourceSelection.Includes(relative, selection)) continue;
                var outputEntry = new System.Formats.Tar.PaxTarEntry(entry.EntryType, relative)
                {
                    ModificationTime = entry.ModificationTime,
                    Mode = entry.Mode,
                    Uid = entry.Uid,
                    Gid = entry.Gid
                };
                if (!string.IsNullOrWhiteSpace(entry.LinkName)) outputEntry.LinkName = entry.LinkName;
                if (entry.DataStream is not null)
                    outputEntry.DataStream = entry.DataStream.CanSeek ? entry.DataStream : new KnownLengthReadStream(entry.DataStream, entry.Length);
                writer.WriteEntry(outputEntry);
            }
            return true;
        }, cancellationToken);
    }

    private async Task<T> ReadDockerVolumeArchiveAsync<T>(string volumeName, Func<Stream, Task<T>> read, CancellationToken cancellationToken)
    {
        using var client = CreateDockerClient();
        var volume = await client.GetAsync($"/v1.41/volumes/{Uri.EscapeDataString(volumeName)}", cancellationToken);
        if (!volume.IsSuccessStatusCode) throw new InvalidOperationException($"Docker-Volume '{volumeName}' wurde nicht gefunden.");

        var create = await client.PostAsJsonAsync("/v1.41/containers/create", new
        {
            Image = "busybox:latest",
            Cmd = new[] { "sleep", "3600" },
            HostConfig = new { AutoRemove = false, Mounts = new[] { new { Type = "volume", Source = volumeName, Target = "/source", ReadOnly = true } } }
        }, cancellationToken);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<ContainerResponse>(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(created?.Id)) throw new InvalidOperationException("Docker lieferte keine Container-ID.");

        try
        {
            (await client.PostAsync($"/v1.41/containers/{created.Id}/start", null, cancellationToken)).EnsureSuccessStatusCode();
            using var response = await client.GetAsync($"/v1.41/containers/{created.Id}/archive?path=/source", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await read(input);
        }
        finally
        {
            try { await client.DeleteAsync($"/v1.41/containers/{created.Id}?force=true", CancellationToken.None); } catch (Exception ex) { logger.LogDebug(ex, "Temporary Docker archive container cleanup failed"); }
        }
    }

    private static string NormalizeDockerArchivePath(string value)
    {
        var normalized = value.Replace('\\', '/').Trim('/');
        if (normalized.Equals("source", StringComparison.OrdinalIgnoreCase)) return "";
        return normalized.StartsWith("source/", StringComparison.OrdinalIgnoreCase) ? normalized[7..] : normalized;
    }

    private async Task ApplyDockerVolumeRestoreAsync(string volumeName, string archivePath, CancellationToken cancellationToken)
    {
        using var client = CreateDockerClient();
        var volume = await client.GetAsync($"/v1.41/volumes/{Uri.EscapeDataString(volumeName)}", cancellationToken);
        if (!volume.IsSuccessStatusCode) throw new InvalidOperationException($"Docker-Volume '{volumeName}' wurde nicht gefunden.");

        var create = await client.PostAsJsonAsync("/v1.41/containers/create", new
        {
            Image = "busybox:latest",
            Cmd = new[] { "sleep", "3600" },
            HostConfig = new { AutoRemove = false, Mounts = new[] { new { Type = "volume", Source = volumeName, Target = "/target", ReadOnly = false } } }
        }, cancellationToken);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<ContainerResponse>(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(created?.Id)) throw new InvalidOperationException("Docker lieferte keine Container-ID.");

        try
        {
            (await client.PostAsync($"/v1.41/containers/{created.Id}/start", null, cancellationToken)).EnsureSuccessStatusCode();
            await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/v1.41/containers/{created.Id}/archive?path=/target")
            {
                Content = new StreamContent(input)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            try { await client.DeleteAsync($"/v1.41/containers/{created.Id}?force=true", CancellationToken.None); }
            catch (Exception ex) { logger.LogDebug(ex, "Temporary Docker restore container cleanup failed"); }
        }
    }

    private static HttpClient CreateDockerClient()
    {
        var socket = Environment.GetEnvironmentVariable("DOCKER_SOCKET_PATH") ?? "/var/run/docker.sock";
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var endpoint = new System.Net.Sockets.UnixDomainSocketEndPoint(socket);
                var socketClient = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, 0);
                await socketClient.ConnectAsync(endpoint, cancellationToken);
                return new System.Net.Sockets.NetworkStream(socketClient, ownsSocket: true);
            }
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    private static bool IsPathWithin(string directory, string candidate)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Throw a controlled IOException if the transfer-cache drive is at/below the MatBu free-space reserve.
    /// Used by the primary receive paths, which otherwise have no disk bound, so the volume is never filled
    /// to zero: a resumable abort + retry instead of a raw ENOSPC failure.
    /// </summary>
    public void EnsureCacheFreeSpace(long pendingBytes) => EnsureFreeSpace(CacheDirectory, pendingBytes);

    /// <summary>
    /// Throw a controlled IOException if the drive holding <paramref name="path"/> is at/below the MatBu
    /// free-space reserve. Used where data lands (transfer cache, or the target when streaming directly to
    /// it) so the volume is never filled to zero: a resumable abort + retry instead of a raw ENOSPC failure.
    /// </summary>
    public void EnsureFreeSpace(string path, long pendingBytes)
    {
        var drive = ResolveDrive(path);
        var minimumFreeSpace = ResolveMinimumFreeSpace(drive);
        if (drive.AvailableFreeSpace <= minimumFreeSpace + Math.Max(0, pendingBytes))
            throw new IOException(
                $"Transfer kontrolliert abgebrochen: Auf '{drive.Name}' sind nur noch {FormatBytes(drive.AvailableFreeSpace)} frei. " +
                $"Die MatBu-Sicherheitsreserve von {FormatBytes(minimumFreeSpace)} wird nicht unterschritten.");
    }

    private static DriveInfo ResolveDrive(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root)) throw new IOException($"Datentraeger fuer den Transferpfad '{path}' konnte nicht ermittelt werden.");
        return new DriveInfo(root);
    }

    private long ResolveMinimumFreeSpace(DriveInfo drive)
    {
        var gib = (transferSettings?.Read() ?? TransferSettings.FromEnvironmentDefaults()).MinFreeSpaceGiB;
        if (gib > 0)
            return Math.Max(512L * MiB, (long)Math.Ceiling(gib * GiB));
        return Math.Clamp(drive.TotalSize / 20, 512L * MiB, 5L * GiB);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= GiB) return $"{bytes / (double)GiB:N1} GiB";
        return $"{bytes / (double)MiB:N0} MiB";
    }

    private static Stream CreateCompressionStream(Stream output, BackupCompression compression) => compression switch
    {
        BackupCompression.None => new PassthroughWriteStream(output),
        BackupCompression.Fast => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
        BackupCompression.Balanced => new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true),
        BackupCompression.Maximum => new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(compression))
    };

    private static Task<long> EstimateLocalTarBytesAsync(string directory, IReadOnlyList<string> selection, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!Directory.Exists(directory)) return 0L;
        long total = 1024;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
        foreach (var path in Directory.EnumerateFiles(directory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (!SourceSelection.Includes(relative, selection)) continue;
            try { var length = new FileInfo(path).Length; total += 512 + ((length + 511) / 512 * 512); } catch { }
        }
        return total;
    }, cancellationToken);

    private static void CreateLocalTar(string rootPath, Stream output, IReadOnlyList<string> selection, CancellationToken cancellationToken)
    {
        if (selection.Count == 0)
        {
            System.Formats.Tar.TarFile.CreateFromDirectory(rootPath, output, includeBaseDirectory: false);
            return;
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        using var writer = new System.Formats.Tar.TarWriter(output, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true);
        foreach (var selected in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selectedPath = Path.GetFullPath(Path.Combine(root, selected.Replace('/', Path.DirectorySeparatorChar)));
            if (!selectedPath.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !Directory.Exists(selectedPath))
                throw new DirectoryNotFoundException($"Der ausgewählte Quellordner wurde nicht gefunden: {selected}");
            writer.WriteEntry(selectedPath, selected);
            foreach (var directory in Directory.EnumerateDirectories(selectedPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteEntry(directory, Path.GetRelativePath(root, directory).Replace('\\', '/'));
            }
            foreach (var file in Directory.EnumerateFiles(selectedPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteEntry(file, Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }
    }

    private sealed class CountingWriteStream(Stream inner, Action changed, Action<int>? beforeWrite = null, IncrementalHash? hasher = null) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => BytesWritten; public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { beforeWrite?.Invoke(count); inner.Write(buffer, offset, count); hasher?.AppendData(buffer.AsSpan(offset, count)); BytesWritten += count; changed(); }
        public override void Write(ReadOnlySpan<byte> buffer) { beforeWrite?.Invoke(buffer.Length); inner.Write(buffer); hasher?.AppendData(buffer); BytesWritten += buffer.Length; changed(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { beforeWrite?.Invoke(buffer.Length); await inner.WriteAsync(buffer, cancellationToken); hasher?.AppendData(buffer.Span); BytesWritten += buffer.Length; changed(); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteLegacyAsync(buffer, offset, count, cancellationToken);
        private async Task WriteLegacyAsync(byte[] buffer, int offset, int count, CancellationToken token) { beforeWrite?.Invoke(count); await inner.WriteAsync(buffer.AsMemory(offset, count), token); hasher?.AppendData(buffer.AsSpan(offset, count)); BytesWritten += count; changed(); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class PassthroughWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => inner.WriteAsync(buffer, token);
        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class KnownLengthReadStream(Stream inner, long length) : Stream
    {
        private long _position;
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set { if (value != _position) throw new NotSupportedException("Der Docker-TAR-Stream kann nicht zurückgespult werden."); } }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, (int)Math.Min(count, length - _position)); _position += read; return read; }
        public override int Read(Span<byte> buffer) { var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, length - _position)]); _position += read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - _position)], token); _position += read; return read; }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => length + offset, _ => _position };
            if (target != _position) throw new NotSupportedException("Der Docker-TAR-Stream kann nicht zurückgespult werden.");
            return _position;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }

    private sealed record ContainerResponse(string Id);
    private sealed record ExecResponse(string Id);
    private sealed record ExecInspectResponse(int ExitCode);
}
