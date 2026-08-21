using System.Text.Json;
using System.Text.Json.Serialization;
using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var dataPath = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var dataProtectionKeysPath = Path.Combine(dataPath, "keys");
Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services
    .AddDataProtection()
    .SetApplicationName("MatBu")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
builder.Services.AddSingleton<PersistentStore>();
builder.Services.AddRazorPages();
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    if (!string.Equals(Environment.GetEnvironmentVariable("MATBU_TRUST_FORWARD_HEADERS"), "true", StringComparison.OrdinalIgnoreCase)) return;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddHostedService<BackupScheduler>();
builder.Services.AddHostedService<DockerVolumeBackupWorker>();
builder.Services.AddSingleton<SmbClientService>();
builder.Services.AddSingleton<ProxmoxService>();
builder.Services.AddSingleton<ProxmoxBackupServerService>();
builder.Services.AddSingleton<ProxmoxNativeBackupService>();
builder.Services.AddSingleton<ObjectConnectivityTester>();
builder.Services.AddHttpClient(nameof(SecondaryGatewayClient), client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("SecondaryTransfer", client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient("PrimaryConnection", client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient("Notifications", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<SecondaryGatewayClient>();
builder.Services.AddSingleton<SecondaryCommandService>();
builder.Services.AddSingleton<ArchiveService>();
builder.Services.AddSingleton<SourceBrowserService>();
builder.Services.AddSingleton<GatewayTransferService>();
builder.Services.AddSingleton<IncrementalSourceService>();
builder.Services.AddSingleton<ReverseIncrementalRepositoryService>();
builder.Services.AddSingleton<BackupRetentionService>();
builder.Services.AddSingleton<RestoreArchiveService>();
builder.Services.AddSingleton<RestoreExecutionService>();
builder.Services.AddSingleton<BackupTaskExecutor>();
builder.Services.AddSingleton<DockerConsistencyService>();
builder.Services.AddHostedService<ConsistencyRecoveryWorker>();
builder.Services.AddHostedService<TransferCacheMaintenanceService>();
builder.Services.AddSingleton<NotificationSettingsStore>();
builder.Services.AddSingleton<TransferSettingsStore>();
builder.Services.AddSingleton<GeneralSettingsStore>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddHostedService<NotificationDispatcher>();
builder.Services.AddHostedService<SecondaryConnectionWorker>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.UseExceptionHandler();
app.UseForwardedHeaders();
if (app.Environment.IsProduction()) app.UseHsts();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        !context.Request.Path.StartsWithSegments("/api/auth/login") &&
        !context.Request.Path.StartsWithSegments("/api/auth/me") &&
        !context.Request.Path.StartsWithSegments("/api/auth/logout"))
    {
        var store = context.RequestServices.GetRequiredService<PersistentStore>();
        var monitoringAccess = context.Request.Path.StartsWithSegments("/api/monitoring/health") && store.IsMonitoringTokenValid(context.Request.Headers["X-MatBu-Token"].FirstOrDefault());
        var gatewayAccess = context.Request.Path.StartsWithSegments("/api/gateway") && store.IsInstanceTokenValid(context.Request.Headers["X-MatBu-Instance-Token"].FirstOrDefault());
        var secondaryConnectionAccess = context.Request.Path.StartsWithSegments("/api/secondary") && store.IsRegisteredSecondaryTokenValid(context.Request.Headers["X-MatBu-Instance-Token"].FirstOrDefault());
        if (!monitoringAccess && !gatewayAccess && !secondaryConnectionAccess && !store.IsSessionValid(context.Request.Cookies["matbu_session"]))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var sessionData = store.Read();
        var session = sessionData.UserSessions.FirstOrDefault(x => x.Token == context.Request.Cookies["matbu_session"]);
        var user = session is null ? null : sessionData.Users.FirstOrDefault(x => x.Id == session.UserId);
        var isWrite = context.Request.Method is "POST" or "PUT" or "DELETE";
        if (context.Request.Path.StartsWithSegments("/api/users") && user?.Role != UserRole.Admin ||
            isWrite && (context.Request.Path.StartsWithSegments("/api/tasks") || context.Request.Path.StartsWithSegments("/api/objects")) && user?.Role == UserRole.User)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next();
});

app.MapGet("/api/summary", (PersistentStore store) =>
{
    var data = store.Read();
    return Results.Ok(new
    {
        activeTasks = data.Tasks.Count(t => t.Enabled),
        healthyObjects = data.Objects.Count(o => o.Status == ObjectStatus.Healthy),
        attentionObjects = data.Objects.Count(o => o.Status != ObjectStatus.Healthy),
        lastBackup = data.Tasks.Where(t => t.LastRun is not null).OrderByDescending(t => t.LastRun).FirstOrDefault()?.LastRun,
        transfer = new[] { 68, 74, 72, 81, 78, 86, 91 }
    });
});
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTimeOffset.UtcNow }));
app.MapGet("/api/monitoring/health", (PersistentStore store) =>
{
    var data = store.Read();
    var failureCutoff = DateTimeOffset.UtcNow.AddHours(-24);
    var unhealthyObjects = data.Objects.Where(x => x.Status != ObjectStatus.Healthy).Select(x => new { x.Id, x.Name, status = x.Status.ToString(), x.Detail }).ToList();
    var failedJobs = data.TransferJobs.Count(x =>
        x.State.Equals("Fehler", StringComparison.OrdinalIgnoreCase) &&
        x.UpdateDate >= failureCutoff);
    var failedTasks = data.Tasks.Where(x => x.State.Equals("Fehler", StringComparison.OrdinalIgnoreCase))
        .Select(x => new { x.Id, x.Name, x.LastRun })
        .ToList();
    var healthy = unhealthyObjects.Count == 0 && failedJobs == 0 && failedTasks.Count == 0;
    var payload = new
    {
        status = healthy ? "Healthy" : "Degraded",
        timestamp = DateTimeOffset.UtcNow,
        objects = new { total = data.Objects.Count, unhealthy = unhealthyObjects },
        tasks = new { failed = failedTasks },
        jobs = new
        {
            failed = failedJobs,
            failureWindowHours = 24,
            active = data.TransferJobs.Count(x => x.State == "Running")
        }
    };
    return Results.Json(payload, statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});
app.MapPost("/api/gateway/object-test", async (GatewayObjectTestRequest request, ObjectConnectivityTester tester, CancellationToken cancellationToken) =>
{
    var item = new BackupObject { Kind = request.Kind, Direction = request.Direction, Location = request.Location };
    return Results.Ok(await tester.TestAsync(item, request.SmbUsername, request.SmbPassword, cancellationToken));
});
app.MapPost("/api/gateway/transfer/{transferId}/source", async (string transferId, GatewaySourceRequest request, GatewayTransferService transfers, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!string.Equals(transferId, request.TransferId, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { message = "Transfer-ID stimmt nicht überein." });
    var archivePath = await transfers.PrepareSourceArchiveAsync(request, cancellationToken);
    var length = new FileInfo(archivePath).Length;
    var offset = Math.Max(0, request.Offset);
    if (offset > length) { context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable; context.Response.Headers.ContentRange = $"bytes */{length}"; return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable); }
    var stream = transfers.OpenSourceRange(archivePath, offset);
    context.Response.ContentLength = length - offset;
    if (offset > 0)
    {
        context.Response.StatusCode = StatusCodes.Status206PartialContent;
        context.Response.Headers.ContentRange = $"bytes {offset}-{length - 1}/{length}";
    }
    return Results.Stream(stream, "application/x-tar");
});
app.MapGet("/api/gateway/transfer/{transferId}/upload-status", (string transferId, GatewayTransferService transfers) => Results.Ok(new { offset = transfers.GetUploadOffset(transferId) }));
app.MapPut("/api/gateway/transfer/{transferId}/upload", async (string transferId, HttpContext context, GatewayTransferService transfers, CancellationToken cancellationToken) =>
{
    if (!long.TryParse(context.Request.Headers["X-MatBu-Task-Id"], out var taskId) ||
        !Enum.TryParse<ObjectKind>(context.Request.Headers["X-MatBu-Target-Kind"], true, out var kind) ||
        string.IsNullOrWhiteSpace(context.Request.Headers["X-MatBu-Target-Location"].FirstOrDefault()) ||
        !long.TryParse(context.Request.Headers["X-MatBu-Transfer-Offset"], out var offset))
        return Results.BadRequest(new { message = "Transfer-Metadaten fehlen." });
    _ = Enum.TryParse<BackupCompression>(context.Request.Headers["X-MatBu-Target-Compression"], true, out var compression);
    var target = new GatewayTargetRequest(taskId, kind, context.Request.Headers["X-MatBu-Target-Location"].First()!, context.Request.Headers["X-MatBu-Target-Smb-Username"].FirstOrDefault(), context.Request.Headers["X-MatBu-Target-Smb-Password"].FirstOrDefault(), compression, context.Request.Headers["X-MatBu-Transfer-Sha256"].FirstOrDefault() ?? "");
    var final = bool.TryParse(context.Request.Headers["X-MatBu-Transfer-Final"], out var isFinal) && isFinal;
    var result = await transfers.ReceiveUploadAsync(transferId, target, offset, final, context.Request.Body, cancellationToken);
    return Results.Ok(result);
});
app.MapPost("/api/secondary/poll", (HttpContext context, SecondaryCommandService commands) =>
{
    var command = commands.LeaseNext(context.Request.Headers["X-MatBu-Instance-Token"].FirstOrDefault() ?? "");
    return command is null ? Results.NoContent() : Results.Ok(command);
});
app.MapPost("/api/secondary/commands/{commandId:long}/complete", async (long commandId, SecondaryCommandCompletion completion, HttpContext context, SecondaryCommandService commands) =>
{
    var changed = commands.Complete(context.Request.Headers["X-MatBu-Instance-Token"].FirstOrDefault() ?? "", commandId, completion.Success, completion.ResultJson, completion.Error);
    return changed ? Results.Ok() : Results.NotFound();
});
app.MapPost("/api/secondary/commands/{commandId:long}/progress", (long commandId, SecondaryCommandProgress progress, HttpContext context, SecondaryCommandService commands) =>
{
    // Back-channel stop directive: if the command was cancelled, tell the secondary to abort on its heartbeat.
    if (commands.IsCancelRequested(commandId)) return Results.Conflict(new { message = "Kommando abgebrochen." });
    var changed = commands.UpdateProgress(context.Request.Headers["X-MatBu-Instance-Token"].FirstOrDefault() ?? "", commandId, progress);
    return changed ? Results.Ok() : Results.NotFound();
});
app.MapGet("/api/secondary/transfers/{transferId}/source-status", async (string transferId, string? sha256, GatewayTransferService transfers, CancellationToken cancellationToken) => Results.Ok(new { offset = await transfers.GetSourceOffsetAsync(transferId, sha256 ?? "", cancellationToken) }));
app.MapGet("/api/secondary/transfers/{transferId}/stream-status", (string transferId, GatewayTransferService transfers) =>
    Results.Ok(transfers.GetIncomingSourceStatus(transferId)));
app.MapGet("/api/secondary/transfers/{transferId}/stream", (string transferId, long offset, long? maxBytes, GatewayTransferService transfers, HttpContext context) =>
{
    var status = transfers.GetIncomingSourceStatus(transferId);
    if (offset < 0 || offset > status.AvailableBytes)
    {
        context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
        context.Response.Headers.ContentRange = $"bytes */{status.AvailableBytes}";
        return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
    }
    var length = Math.Min(Math.Clamp(maxBytes ?? 4 * 1024 * 1024, 1, 16 * 1024 * 1024), status.AvailableBytes - offset);
    context.Response.ContentLength = length;
    return Results.Stream(transfers.OpenIncomingSourceRange(transferId, offset, length), "application/octet-stream");
});
app.MapPut("/api/secondary/transfers/{transferId}/source", async (string transferId, HttpContext context, GatewayTransferService transfers, CancellationToken cancellationToken) =>
{
    if (!long.TryParse(context.Request.Headers["X-MatBu-Transfer-Offset"], out var offset) || !long.TryParse(context.Request.Headers["X-MatBu-Transfer-Job-Id"], out var jobId) || !long.TryParse(context.Request.Headers["X-MatBu-Transfer-Total"], out var total)) return Results.BadRequest(new { message = "Transfer-Metadaten fehlen." });
    var final = bool.TryParse(context.Request.Headers["X-MatBu-Transfer-Final"], out var isFinal) && isFinal;
    var sha256 = context.Request.Headers["X-MatBu-Transfer-Sha256"].FirstOrDefault() ?? "";
    return Results.Ok(await transfers.ReceiveSourceChunkAsync(transferId, offset, final, jobId, total, sha256, context.Request.Body, cancellationToken));
});
app.MapPost("/api/secondary/transfers/{transferId}/incremental-manifest", async (
    string transferId,
    IncrementalBackupManifest manifest,
    PersistentStore store,
    IncrementalSourceService incrementalSources,
    ReverseIncrementalRepositoryService repository,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(transferId, manifest.TransferId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "Transfer-ID des Incremental-Manifests stimmt nicht überein." });
    var data = store.Read();
    var job = data.TransferJobs.FirstOrDefault(item => item.TransferId.Equals(transferId, StringComparison.OrdinalIgnoreCase));
    var task = job is null ? null : data.Tasks.FirstOrDefault(item => item.Id == job.TaskId && item.Token == manifest.TaskToken);
    var target = task is null ? null : data.Objects.FirstOrDefault(item => item.Id == task.TargetId);
    var targetInstance = target is null ? null : data.Instances.FirstOrDefault(item => item.Id == target.InstanceId);
    if (job is null || task is null || target is null || targetInstance is null)
        return Results.BadRequest(new { message = "Der Incremental-Transfer gehört zu keinem aktiven Job." });

    var repositoryKey = repository.BuildRepositoryKey(task, target, targetInstance);
    var previous = await repository.LoadPreviousManifestAsync(task.Token, cancellationToken);
    var baseline = manifest.Method == BackupMethod.Differential
        ? await repository.LoadBaselineManifestAsync(task.Token, cancellationToken)
        : null;
    var comparison = manifest.Method == BackupMethod.Differential ? baseline : previous;
    manifest.ParentSnapshotToken = previous?.SnapshotToken ?? "";
    manifest.BaselineSnapshotToken = baseline?.SnapshotToken ?? comparison?.SnapshotToken ?? manifest.SnapshotToken;
    manifest.ChainDepth = previous is null ? 0 : previous.ChainDepth + 1;
    incrementalSources.ApplyPreviousManifest(manifest, comparison, repositoryKey);
    if (manifest.Method == BackupMethod.Differential)
        IncrementalSourceService.MarkChunksNeededForTransition(manifest, previous);
    await IncrementalManifestJson.WriteAsync(incrementalSources.ManifestPath(transferId), manifest, cancellationToken);
    var missing = incrementalSources.FindMissingChangedHashes(manifest, transferId);
    return Results.Ok(new IncrementalManifestUploadResult(
        true,
        missing,
        manifest.TotalBytes,
        manifest.StoredBytes,
        manifest.ReusedBytes,
        $"Manifest angenommen; {missing.Count:N0} geänderte Chunks fehlen."));
});
app.MapGet("/api/secondary/transfers/{transferId}/incremental-manifest", async (
    string transferId,
    IncrementalSourceService incrementalSources,
    CancellationToken cancellationToken) =>
{
    var path = incrementalSources.ManifestPath(transferId);
    if (!File.Exists(path)) return Results.NotFound();
    return Results.Ok(await IncrementalManifestJson.ReadAsync(path, cancellationToken));
});
app.MapPut("/api/secondary/transfers/{transferId}/incremental-chunks/{hash}", async (
    string transferId,
    string hash,
    HttpContext context,
    IncrementalSourceService incrementalSources,
    CancellationToken cancellationToken) =>
{
    var length = await incrementalSources.ReceiveChunkAsync(transferId, hash, context.Request.Body, cancellationToken);
    return Results.Ok(new { hash, length });
});
app.MapGet("/api/secondary/transfers/{transferId}/incremental-chunks/{hash}", (
    string transferId,
    string hash,
    IncrementalSourceService incrementalSources) =>
{
    var path = incrementalSources.ChunkPath(transferId, hash);
    return File.Exists(path)
        ? Results.Stream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), "application/octet-stream", enableRangeProcessing: true)
        : Results.NotFound();
});
app.MapGet("/api/secondary/transfers/{transferId}/target", (string transferId, long taskId, long offset, GatewayTransferService transfers, HttpContext context) =>
{
    var archivePath = transfers.ResolveOutgoingTargetArchive(transferId, taskId);
    if (!File.Exists(archivePath)) return Results.NotFound();
    var length = new FileInfo(archivePath).Length;
    if (offset < 0 || offset > length) return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
    context.Response.ContentLength = length - offset;
    if (offset > 0) { context.Response.StatusCode = StatusCodes.Status206PartialContent; context.Response.Headers.ContentRange = $"bytes {offset}-{length - 1}/{length}"; }
    return Results.Stream(transfers.OpenSourceRange(archivePath, offset), "application/x-tar");
});
app.MapGet("/api/monitoring/token", (HttpContext context, PersistentStore store) =>
{
    var data = store.Read();
    var session = data.UserSessions.FirstOrDefault(x => x.Token == context.Request.Cookies["matbu_session"] && x.ExpiresDate > DateTimeOffset.UtcNow);
    var user = session is null ? null : data.Users.FirstOrDefault(x => x.Id == session.UserId);
    return user?.Role == UserRole.Admin ? Results.Ok(new { token = store.GetMonitoringToken() }) : Results.Forbid();
});
app.MapPost("/api/monitoring/token/regenerate", (HttpContext context, PersistentStore store) =>
{
    var data = store.Read();
    var session = data.UserSessions.FirstOrDefault(x => x.Token == context.Request.Cookies["matbu_session"] && x.ExpiresDate > DateTimeOffset.UtcNow);
    var user = session is null ? null : data.Users.FirstOrDefault(x => x.Id == session.UserId);
    return user?.Role == UserRole.Admin ? Results.Ok(new { token = store.RegenerateMonitoringToken() }) : Results.Forbid();
});

app.MapGet("/api/tasks", (HttpContext context, PersistentStore store) =>
{
    var data = store.Read();
    var isAdmin = CurrentUser(context, store)?.Role == UserRole.Admin;
    foreach (var task in data.Tasks)
    {
        task.LabelIds = data.BackupTaskLabels.Where(item => item.BackupTaskId == task.Id).Select(item => item.JobLabelId).ToList();
        if (!isAdmin) { task.PreBackupCommand = ""; task.PostBackupCommand = ""; }
    }
    return Results.Ok(data.Tasks);
});
app.MapGet("/api/transfer-jobs", (PersistentStore store) => Results.Ok(store.Read().TransferJobs.OrderByDescending(x => x.UpdateDate)));
app.MapGet("/api/transfer-jobs/{id:long}", (long id, PersistentStore store) =>
{
    var data = store.Read();
    var job = data.TransferJobs.FirstOrDefault(x => x.Id == id);
    return job is null
        ? Results.NotFound()
        : Results.Ok(new
        {
            job,
            steps = data.JobSteps.Where(x => x.TransferJobId == id).OrderBy(x => x.Sequence)
        });
});
app.MapPost("/api/transfer-jobs/{id:long}/cancel", (long id, HttpContext context, PersistentStore store) =>
{
    // Role gate is in-handler: the shared write-guard middleware only covers /api/tasks and /api/objects.
    var user = CurrentUser(context, store);
    if (user is null) return Results.Unauthorized();
    // Results.Forbid() throws when no auth scheme is registered (there is none); return an explicit 403.
    if (user.Role == UserRole.User) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var job = store.Read().TransferJobs.FirstOrDefault(x => x.Id == id);
    if (job is null) return Results.NotFound();
    if (job.State != "Running") return Results.Conflict(new { message = "Nur laufende Ausführungen können abgebrochen werden." });
    var applied = false;
    store.Update(data =>
    {
        var current = data.TransferJobs.FirstOrDefault(x => x.Id == id);
        if (current is null || current.State != "Running") return; // re-check inside the transaction (cancel-vs-completion race)
        current.CancelRequested = true;
        current.Phase = JobPhase.Cancelling;
        current.UpdateDate = DateTimeOffset.UtcNow;
        foreach (var command in data.SecondaryCommands.Where(c => c.TransferId == current.TransferId && c.State is "Queued" or "Running"))
        {
            command.CancelRequested = true;
            command.UpdateDate = DateTimeOffset.UtcNow;
        }
        applied = true;
    });
    return applied
        ? Results.Ok(new { message = "Abbruch angefordert." })
        : Results.Conflict(new { message = "Nur laufende Ausführungen können abgebrochen werden." });
});
app.MapPost("/api/tasks/{id:long}/run", (long id, PersistentStore store) =>
{
    var task = store.Read().Tasks.FirstOrDefault(x => x.Id == id);
    if (task is null) return Results.NotFound();
    if (task.State == "Geplant") return Results.Conflict(new { message = "Der Task ist bereits zur Ausführung eingeplant." });
    store.Update(data =>
    {
        var current = data.Tasks.First(x => x.Id == id);
        current.State = "Geplant";
        current.NextRetryDate = null;
        current.UpdateDate = DateTimeOffset.UtcNow;
    });
    return Results.Ok(new { message = "Task wurde zur sofortigen Ausführung eingeplant." });
});
app.MapPost("/api/tasks", (BackupTask task, HttpContext context, PersistentStore store, GeneralSettingsStore generalSettings) =>
{
    var isAdmin = CurrentUser(context, store)?.Role == UserRole.Admin;
    if (task.ConsistencyMode != BackupConsistencyMode.None && !isAdmin) return Results.Forbid();
    var consistencyError = ValidateConsistency(task);
    if (consistencyError is not null) return Results.BadRequest(new { message = consistencyError });
    if (!BackupSchedule.TryParse(task.Schedule, out _))
        return Results.BadRequest(new { message = "Der Zeitplan ist ungültig." });
    task.Schedule = BackupSchedule.Normalize(task.Schedule);
    task.SourceSelectionJson = SourceSelection.Serialize(SourceSelection.Parse(task.SourceSelectionJson));
    if (BackupMethodPolicy.IsChunked(task.Method) && task.ChunkSizeMiB is not (4 or 8 or 16 or 32))
        return Results.BadRequest(new { message = "Reverse Incremental benötigt 4, 8, 16 oder 32 MiB Chunkgröße." });
    if (BackupMethodPolicy.IsChunked(task.Method) || task.Method == BackupMethod.ProxmoxNative) task.Compression = BackupCompression.None;
    if (task.MaxRetryAttempts is < 1 or > 20 || task.RetryDelayMinutes is < 1 or > 1440)
        return Results.BadRequest(new { message = "Retry-Konfiguration ungültig: 1–20 Versuche und 1–1440 Minuten Basiswartezeit sind erlaubt." });
    var data = store.Read();
    var labelIds = task.LabelIds.Distinct().ToList();
    if (labelIds.Any(labelId => !data.JobLabels.Any(label => label.Id == labelId))) return Results.BadRequest(new { message = "Mindestens ein ausgewählter Tag existiert nicht." });
    var source = data.Objects.FirstOrDefault(x => x.Id == task.SourceId);
    var target = data.Objects.FirstOrDefault(x => x.Id == task.TargetId);
    if (source is null || target is null) return Results.BadRequest(new { message = "Quelle und Ziel müssen vorhandene Objekte sein." });
    if (source.Direction == ObjectDirection.Target) return Results.BadRequest(new { message = "Das ausgewählte Objekt kann nicht als Quelle verwendet werden." });
    if (target.Direction == ObjectDirection.Source) return Results.BadRequest(new { message = "Das ausgewählte Objekt kann nicht als Ziel verwendet werden." });
    if (source.Id == target.Id) return Results.BadRequest(new { message = "Quelle und Ziel müssen unterschiedlich sein." });
    var routeError = BackupRoutePolicy.Validate(task, source, target);
    if (routeError is not null) return Results.BadRequest(new { message = routeError });
    task.Id = store.NextId(data.Tasks.Select(x => x.Id));
    task.CreateDate = DateTimeOffset.UtcNow;
    task.UpdateDate = task.CreateDate;
    task.NextRunDate = task.Enabled ? BackupSchedule.GetNextOccurrenceUtc(task.Schedule, task.CreateDate, generalSettings.ResolveTimeZone()) : null;
    store.Update(current =>
    {
        current.Tasks.Add(task);
        var nextAssignmentId = store.NextId(current.BackupTaskLabels.Select(item => item.Id));
        foreach (var labelId in labelIds)
            current.BackupTaskLabels.Add(new BackupTaskLabel { Id = nextAssignmentId++, BackupTaskId = task.Id, JobLabelId = labelId, CreateDate = task.CreateDate, UpdateDate = task.CreateDate });
    });
    return Results.Created($"/api/tasks/{task.Id}", task);
});
app.MapPut("/api/tasks/{id:long}", (long id, BackupTask task, HttpContext context, PersistentStore store, GeneralSettingsStore generalSettings) =>
{
    var isAdmin = CurrentUser(context, store)?.Role == UserRole.Admin;
    if (!BackupSchedule.TryParse(task.Schedule, out _))
        return Results.BadRequest(new { message = "Der Zeitplan ist ungültig." });
    task.Schedule = BackupSchedule.Normalize(task.Schedule);
    task.SourceSelectionJson = SourceSelection.Serialize(SourceSelection.Parse(task.SourceSelectionJson));
    if (BackupMethodPolicy.IsChunked(task.Method) && task.ChunkSizeMiB is not (4 or 8 or 16 or 32))
        return Results.BadRequest(new { message = "Reverse Incremental benötigt 4, 8, 16 oder 32 MiB Chunkgröße." });
    if (BackupMethodPolicy.IsChunked(task.Method) || task.Method == BackupMethod.ProxmoxNative) task.Compression = BackupCompression.None;
    if (task.MaxRetryAttempts is < 1 or > 20 || task.RetryDelayMinutes is < 1 or > 1440)
        return Results.BadRequest(new { message = "Retry-Konfiguration ungültig: 1–20 Versuche und 1–1440 Minuten Basiswartezeit sind erlaubt." });
    var data = store.Read();
    var labelIds = task.LabelIds.Distinct().ToList();
    if (labelIds.Any(labelId => !data.JobLabels.Any(label => label.Id == labelId))) return Results.BadRequest(new { message = "Mindestens ein ausgewählter Tag existiert nicht." });
    if (!data.Tasks.Any(x => x.Id == id)) return Results.NotFound();
    var existingTask = data.Tasks.First(x => x.Id == id);
    if (!isAdmin)
    {
        if (existingTask.ConsistencyMode != task.ConsistencyMode || existingTask.ConsistencyContainerNames != task.ConsistencyContainerNames || existingTask.ConsistencyTimeoutSeconds != task.ConsistencyTimeoutSeconds)
            return Results.Forbid();
        task.PreBackupCommand = existingTask.PreBackupCommand;
        task.PostBackupCommand = existingTask.PostBackupCommand;
    }
    var consistencyError = ValidateConsistency(task);
    if (consistencyError is not null) return Results.BadRequest(new { message = consistencyError });
    var source = data.Objects.FirstOrDefault(x => x.Id == task.SourceId);
    var target = data.Objects.FirstOrDefault(x => x.Id == task.TargetId);
    if (source is null || target is null) return Results.BadRequest(new { message = "Quelle und Ziel müssen vorhandene Objekte sein." });
    if (source.Direction == ObjectDirection.Target) return Results.BadRequest(new { message = "Das ausgewählte Objekt kann nicht als Quelle verwendet werden." });
    if (target.Direction == ObjectDirection.Source) return Results.BadRequest(new { message = "Das ausgewählte Objekt kann nicht als Ziel verwendet werden." });
    if (source.Id == target.Id) return Results.BadRequest(new { message = "Quelle und Ziel müssen unterschiedlich sein." });
    var routeError = BackupRoutePolicy.Validate(task, source, target);
    if (routeError is not null) return Results.BadRequest(new { message = routeError });
    store.Update(data =>
    {
        var target = data.Tasks.First(x => x.Id == id);
        var now = DateTimeOffset.UtcNow;
        target.Name = task.Name; target.SourceId = task.SourceId; target.TargetId = task.TargetId; target.Method = task.Method; target.Compression = task.Method == BackupMethod.Full ? task.Compression : BackupCompression.None; target.SourceSelectionJson = task.SourceSelectionJson; target.ChunkSizeMiB = task.ChunkSizeMiB; target.Schedule = task.Schedule; target.Retention = task.Retention; target.MaxRetryAttempts = task.MaxRetryAttempts; target.RetryDelayMinutes = task.RetryDelayMinutes; target.ConsistencyMode = task.ConsistencyMode; target.ConsistencyContainerNames = task.ConsistencyContainerNames; target.PreBackupCommand = task.PreBackupCommand; target.PostBackupCommand = task.PostBackupCommand; target.ConsistencyTimeoutSeconds = task.ConsistencyTimeoutSeconds; target.Enabled = task.Enabled; target.NextRunDate = task.Enabled ? BackupSchedule.GetNextOccurrenceUtc(task.Schedule, now, generalSettings.ResolveTimeZone()) : null; target.UpdateDate = now;
        data.BackupTaskLabels.RemoveAll(item => item.BackupTaskId == id);
        var nextAssignmentId = store.NextId(data.BackupTaskLabels.Select(item => item.Id));
        foreach (var labelId in labelIds)
            data.BackupTaskLabels.Add(new BackupTaskLabel { Id = nextAssignmentId++, BackupTaskId = id, JobLabelId = labelId, CreateDate = target.UpdateDate, UpdateDate = target.UpdateDate });
    });
    if (!isAdmin) { task.PreBackupCommand = ""; task.PostBackupCommand = ""; }
    return Results.Ok(task);
});
app.MapDelete("/api/tasks/{id:long}", (long id, PersistentStore store) =>
{
    var removed = false;
    store.Update(data =>
    {
        removed = data.Tasks.RemoveAll(x => x.Id == id) > 0;
        data.BackupTaskLabels.RemoveAll(item => item.BackupTaskId == id);
    });
    return removed ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/objects", (PersistentStore store) =>
{
    var data = store.Read();
    foreach (var item in data.Objects.Where(x => UsesCredentials(x.Kind))) item.SmbUsername = store.GetSmbCredential(item.Id)?.Username;
    return Results.Ok(data.Objects);
});
app.MapGet("/api/objects/{id:long}", (long id, PersistentStore store) =>
{
    var item = store.Read().Objects.FirstOrDefault(x => x.Id == id);
    if (item is null) return Results.NotFound();
    if (UsesCredentials(item.Kind)) item.SmbUsername = store.GetSmbCredential(item.Id)?.Username;
    return Results.Ok(item);
});
app.MapPost("/api/objects", (ObjectUpsertRequest request, PersistentStore store) =>
{
    var item = request.ToObject();
    if (!store.Read().Instances.Any(x => x.Id == item.InstanceId)) return Results.BadRequest(new { message = "Die ausgewählte Instanz existiert nicht." });
    item.Id = store.NextId(store.Read().Objects.Select(x => x.Id));
    item.Status = ObjectStatus.Healthy;
    item.CreateDate = item.UpdateDate = DateTimeOffset.UtcNow;
    store.Update(data => { data.SmbCredentials.RemoveAll(x => x.ObjectId == item.Id); data.Objects.Add(item); if (UsesCredentials(item.Kind) && !string.IsNullOrWhiteSpace(request.SmbUsername) && request.SmbPassword is not null) store.SetSmbCredential(data, item.Id, request.SmbUsername, request.SmbPassword); });
    return Results.Created($"/api/objects/{item.Id}", item);
});
app.MapPut("/api/objects/{id:long}", (long id, ObjectUpsertRequest request, PersistentStore store) =>
{
    var item = request.ToObject();
    var existing = store.Read().Objects.FirstOrDefault(x => x.Id == id);
    if (existing is null) return Results.NotFound();
    if (!store.Read().Instances.Any(x => x.Id == item.InstanceId)) return Results.BadRequest(new { message = "Die ausgewählte Instanz existiert nicht." });
    store.Update(data =>
    {
        var target = data.Objects.First(x => x.Id == id);
        target.Name = item.Name; target.Kind = item.Kind; target.Direction = item.Direction; target.Location = item.Location; target.Detail = item.Detail; target.InstanceId = item.InstanceId; target.UpdateDate = DateTimeOffset.UtcNow;
        if (!UsesCredentials(item.Kind)) data.SmbCredentials.RemoveAll(x => x.ObjectId == id);
        else if (!string.IsNullOrWhiteSpace(request.SmbUsername))
        {
            var password = request.SmbPassword ?? store.GetSmbCredential(id)?.Password;
            if (password is not null) store.SetSmbCredential(data, id, request.SmbUsername, password);
        }
    });
    return Results.Ok(item);
});
app.MapDelete("/api/objects/{id:long}", (long id, PersistentStore store) =>
{
    var removed = false;
    store.Update(data => { removed = data.Objects.RemoveAll(x => x.Id == id) > 0; data.SmbCredentials.RemoveAll(x => x.ObjectId == id); });
    return removed ? Results.NoContent() : Results.NotFound();
});
app.MapPost("/api/objects/{id:long}/test", async (long id, PersistentStore store, SecondaryGatewayClient gateway, ObjectConnectivityTester tester, CancellationToken cancellationToken) =>
{
    var item = store.Read().Objects.FirstOrDefault(x => x.Id == id);
    if (item is null) return Results.NotFound();
    var instance = store.Read().Instances.FirstOrDefault(x => x.Id == item.InstanceId);
    GatewayObjectTestResult result;
    if (instance is null)
    {
        result = new GatewayObjectTestResult(false, "Die zugeordnete MatBu-Instanz wurde nicht gefunden.", 0);
    }
    else if (instance.Role == InstanceRole.Secondary)
    {
        result = await gateway.TestObjectAsync(instance, item, store.GetSmbCredential(item.Id), cancellationToken);
    }
    else
    {
        var credential = store.GetSmbCredential(item.Id);
        result = await tester.TestAsync(item, credential?.Username, credential?.Password, cancellationToken);
    }

    store.Update(data =>
    {
        var current = data.Objects.FirstOrDefault(x => x.Id == id);
        if (current is null) return;
        current.Status = result.Success ? ObjectStatus.Healthy : ObjectStatus.Warning;
        current.LastTestDate = DateTimeOffset.UtcNow;
        current.LastTestMessage = result.Message;
        current.UpdateDate = DateTimeOffset.UtcNow;
    });
    return Results.Ok(result);
});

app.MapGet("/api/users", (PersistentStore store) => Results.Ok(store.Read().Users.Select(x => new { x.Id, x.UserName, x.Role, x.CreateDate })));
app.MapGet("/api/auth/me", (HttpContext context, PersistentStore store) =>
{
    var session = store.Read().UserSessions.FirstOrDefault(x => x.Token == context.Request.Cookies["matbu_session"] && x.ExpiresDate > DateTimeOffset.UtcNow);
    var user = session is null ? null : store.Read().Users.FirstOrDefault(x => x.Id == session.UserId);
    return user is null ? Results.Unauthorized() : Results.Ok(new { user = user.UserName, role = user.Role.ToString() });
});
app.MapPost("/api/auth/login", (LoginRequest request, HttpContext context, PersistentStore store) =>
{
    var user = store.Read().Users.FirstOrDefault(x => x.UserName.Equals(request.UserName, StringComparison.OrdinalIgnoreCase));
    if (user is null || !PersistentStore.VerifyPassword(request.Password, user.PasswordHash)) return Results.Unauthorized();
    var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    store.Update(data => data.UserSessions.Add(new UserSession { Id = store.NextId(data.UserSessions.Select(x => x.Id)), Token = token, UserId = user.Id, ExpiresDate = DateTimeOffset.UtcNow.AddHours(12), CreateDate = DateTimeOffset.UtcNow, UpdateDate = DateTimeOffset.UtcNow }));
    context.Response.Cookies.Append("matbu_session", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, MaxAge = TimeSpan.FromHours(12) });
    return Results.Ok(new { user = user.UserName, role = user.Role.ToString() });
}).RequireRateLimiting("login");
app.MapPost("/api/auth/logout", (HttpContext context, PersistentStore store) => { var token = context.Request.Cookies["matbu_session"]; store.Update(data => data.UserSessions.RemoveAll(x => x.Token == token)); context.Response.Cookies.Delete("matbu_session"); return Results.Ok(); });

static AppUser? CurrentUser(HttpContext context, PersistentStore store)
{
    var data = store.Read();
    var session = data.UserSessions.FirstOrDefault(item => item.Token == context.Request.Cookies["matbu_session"] && item.ExpiresDate > DateTimeOffset.UtcNow);
    return session is null ? null : data.Users.FirstOrDefault(item => item.Id == session.UserId);
}

static bool UsesCredentials(ObjectKind kind) => kind is ObjectKind.Smb or ObjectKind.Proxmox or ObjectKind.ProxmoxBackupServer;

static string? ValidateConsistency(BackupTask task)
{
    task.ConsistencyContainerNames = task.ConsistencyContainerNames?.Trim() ?? "";
    task.PreBackupCommand = task.PreBackupCommand?.Trim() ?? "";
    task.PostBackupCommand = task.PostBackupCommand?.Trim() ?? "";
    if (!Enum.IsDefined(task.ConsistencyMode)) return "Der Konsistenzmodus ist ungültig.";
    if (task.ConsistencyMode == BackupConsistencyMode.None) return null;
    if (task.Method != BackupMethod.Full) return "Anwendungskonsistenz ist derzeit nur für Full-Backups verfügbar.";
    if (task.ConsistencyTimeoutSeconds is < 5 or > 900) return "Der Hook-Timeout muss zwischen 5 und 900 Sekunden liegen.";
    var containers = task.ConsistencyContainerNames.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (task.ConsistencyMode == BackupConsistencyMode.DockerPause && containers.Length == 0) return "Docker Pause benötigt mindestens einen Container.";
    if (task.ConsistencyMode == BackupConsistencyMode.DockerExec && containers.Length != 1) return "Docker Exec benötigt genau einen Container.";
    if (task.ConsistencyMode == BackupConsistencyMode.DockerExec && string.IsNullOrWhiteSpace(task.PreBackupCommand) && string.IsNullOrWhiteSpace(task.PostBackupCommand)) return "Docker Exec benötigt mindestens ein Pre- oder Post-Kommando.";
    return null;
}

app.MapRazorPages();
app.Run();

public record LoginRequest(string UserName, string Password);
public sealed record ObjectUpsertRequest(string Name, ObjectKind Kind, ObjectDirection Direction, string Location, string Detail, string? SmbUsername, string? SmbPassword, long InstanceId = 1)
{
    public BackupObject ToObject() => new() { Name = Name, Kind = Kind, Direction = Direction, Location = Location, Detail = Detail, InstanceId = InstanceId };
}
