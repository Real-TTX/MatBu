using System.Diagnostics;
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
    ILogger<SecondaryConnectionWorker> logger) : BackgroundService
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string _primaryEndpoint = (Environment.GetEnvironmentVariable("MATBU_PRIMARY_ENDPOINT") ?? "").TrimEnd('/');
    private readonly string? _token = Environment.GetEnvironmentVariable("MATBU_INSTANCE_TOKEN");

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

    private async Task HandleAsync(HttpClient client, SecondaryCommandEnvelope command, CancellationToken cancellationToken)
    {
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
                case SecondaryCommandKind.ExportSource:
                {
                    var payload = JsonSerializer.Deserialize<SecondaryExportPayload>(command.PayloadJson) ?? throw new InvalidOperationException("Export-Payload fehlt.");
                    var archive = await transfers.PrepareSourceArchiveAsync(payload.Source, payload.Consistency, cancellationToken);
                    var length = new FileInfo(archive).Length;
                    await PushSourceAsync(client, command, archive, payload.JobId, length, cancellationToken);
                    var metrics = transfers.GetSourceMetrics(command.TransferId);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(metrics), "", cancellationToken);
                    break;
                }
                case SecondaryCommandKind.ImportTarget:
                {
                    var payload = JsonSerializer.Deserialize<SecondaryImportPayload>(command.PayloadJson) ?? throw new InvalidOperationException("Import-Payload fehlt.");
                    var destination = await PullTargetAsync(client, command, payload, cancellationToken);
                    await CompleteAsync(client, command.Id, true, JsonSerializer.Serialize(destination), "", cancellationToken);
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
                    var archive = await PullArchiveAsync(client, command, payload.Target.TaskId, payload.TotalBytes, cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Secondary command {CommandId} failed", command.Id);
            await CompleteAsync(client, command.Id, false, "", ex.Message, CancellationToken.None);
        }
    }

    private async Task PushSourceAsync(HttpClient client, SecondaryCommandEnvelope command, string archivePath, long jobId, long total, CancellationToken cancellationToken)
    {
        var offset = await GetOffsetAsync(client, $"/api/secondary/transfers/{command.TransferId}/source-status", cancellationToken);
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

    private async Task<string> PullTargetAsync(HttpClient client, SecondaryCommandEnvelope command, SecondaryImportPayload payload, CancellationToken cancellationToken)
    {
        var final = await PullArchiveAsync(client, command, payload.Target.TaskId, payload.TotalBytes, cancellationToken);
        try { return await transfers.ApplyTargetArchiveAsync(final, command.TransferId, payload.Target, cancellationToken); }
        finally { try { File.Delete(final); } catch { } }
    }

    private async Task<string> PullArchiveAsync(HttpClient client, SecondaryCommandEnvelope command, long taskId, long expectedTotalBytes, CancellationToken cancellationToken)
    {
        var dataPath = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? "/data";
        var partial = Path.Combine(dataPath, "transfer-cache", $"gateway-upload-{command.TransferId}.tar.partial");
        var final = partial[..^".partial".Length];
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        if (File.Exists(final) && (expectedTotalBytes <= 0 || new FileInfo(final).Length == expectedTotalBytes)) return final;
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
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

    private async Task ProgressAsync(HttpClient client, long commandId, long bytes, long total, long speed, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _primaryEndpoint + $"/api/secondary/commands/{commandId}/progress");
        AddToken(request);
        request.Content = JsonContent.Create(new SecondaryCommandProgress(bytes, total, speed, "Secondary-Verbindung"));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
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

    private sealed record TransferOffsetResponse(long Offset);
}
