using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MatBu.Services;

public sealed record BackupConsistencySettings(
    Models.BackupConsistencyMode Mode,
    string ContainerNames,
    string PreCommand,
    string PostCommand,
    int TimeoutSeconds)
{
    public static BackupConsistencySettings FromTask(Models.BackupTask task) => new(
        task.ConsistencyMode,
        task.ConsistencyContainerNames,
        task.PreBackupCommand,
        task.PostBackupCommand,
        Math.Clamp(task.ConsistencyTimeoutSeconds, 5, 900));
}

public sealed record DockerConsistencyLease(
    string Id,
    Models.BackupConsistencyMode Mode,
    IReadOnlyList<string> PausedContainerIds,
    string ExecContainer);

public sealed class DockerConsistencyService
{
    private readonly ILogger<DockerConsistencyService> _logger;
    private readonly IDataProtector _protector;
    private readonly string _leasePath;
    private readonly object _leaseGate = new();
    private static readonly JsonSerializerOptions LeaseJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DockerConsistencyService(ILogger<DockerConsistencyService> logger, IHostEnvironment environment, IDataProtectionProvider protectionProvider)
    {
        _logger = logger;
        _protector = protectionProvider.CreateProtector("MatBu.DockerConsistencyLeases.v1");
        var directory = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        _leasePath = Path.Combine(directory, "consistency-leases.protected");
    }

    public async Task<DockerConsistencyLease> BeginAsync(BackupConsistencySettings settings, CancellationToken cancellationToken)
    {
        if (settings.Mode == Models.BackupConsistencyMode.None)
            return new("", settings.Mode, [], "");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var lease = settings.Mode switch
        {
            Models.BackupConsistencyMode.DockerPause => await PauseAsync(settings, timeout.Token),
            Models.BackupConsistencyMode.DockerExec => await RunPreCommandAsync(settings, timeout.Token),
            _ => throw new InvalidOperationException("Unbekannter Konsistenzmodus.")
        };
        lease = lease with { Id = Guid.NewGuid().ToString("N") };
        Persist(settings, lease);
        return lease;
    }

    public async Task EndAsync(BackupConsistencySettings settings, DockerConsistencyLease lease, CancellationToken cancellationToken)
    {
        if (lease.Mode == Models.BackupConsistencyMode.None) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        if (lease.Mode == Models.BackupConsistencyMode.DockerPause)
        {
            await UnpauseAllAsync(lease.PausedContainerIds, timeout.Token);
            RemovePersisted(lease.Id);
            return;
        }
        if (lease.Mode == Models.BackupConsistencyMode.DockerExec && !string.IsNullOrWhiteSpace(settings.PostCommand))
            await ExecAsync(lease.ExecContainer, settings.PostCommand, timeout.Token);
        RemovePersisted(lease.Id);
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        var pending = ReadPersisted();
        foreach (var item in pending)
        {
            try
            {
                await EndAsync(item.Settings, item.Lease, cancellationToken);
                _logger.LogWarning("Recovered unfinished Docker consistency lease {LeaseId} ({Mode})", item.Lease.Id, item.Lease.Mode);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to recover Docker consistency lease {LeaseId}; it remains queued for the next restart", item.Lease.Id);
            }
        }
    }

    private async Task<DockerConsistencyLease> PauseAsync(BackupConsistencySettings settings, CancellationToken cancellationToken)
    {
        var names = ParseContainers(settings.ContainerNames);
        if (names.Count == 0) throw new InvalidOperationException("Für Docker Pause ist mindestens ein Container erforderlich.");
        var pausedByMatBu = new List<string>();
        try
        {
            using var client = CreateDockerClient();
            foreach (var name in names)
            {
                var container = await InspectAsync(client, name, cancellationToken);
                EnsureNotSelf(container);
                if (container.State.Paused) continue;
                if (!container.State.Running) throw new InvalidOperationException($"Container '{name}' läuft nicht und kann nicht pausiert werden.");
                using var response = await client.PostAsync($"/v1.41/containers/{Uri.EscapeDataString(container.Id)}/pause", null, cancellationToken);
                response.EnsureSuccessStatusCode();
                pausedByMatBu.Add(container.Id);
            }
            return new("", Models.BackupConsistencyMode.DockerPause, pausedByMatBu, "");
        }
        catch
        {
            try { await UnpauseAllAsync(pausedByMatBu, CancellationToken.None); }
            catch (Exception cleanupException) { _logger.LogError(cleanupException, "Failed to roll back partially paused Docker containers"); }
            throw;
        }
    }

    private async Task<DockerConsistencyLease> RunPreCommandAsync(BackupConsistencySettings settings, CancellationToken cancellationToken)
    {
        var names = ParseContainers(settings.ContainerNames);
        if (names.Count != 1) throw new InvalidOperationException("Für Docker Exec muss genau ein Container angegeben werden.");
        using var client = CreateDockerClient();
        var container = await InspectAsync(client, names[0], cancellationToken);
        EnsureNotSelf(container);
        if (!container.State.Running || container.State.Paused) throw new InvalidOperationException($"Container '{names[0]}' ist nicht ausführbar.");
        if (string.IsNullOrWhiteSpace(settings.PreCommand) && string.IsNullOrWhiteSpace(settings.PostCommand))
            throw new InvalidOperationException("Für Docker Exec ist mindestens ein Pre- oder Post-Kommando erforderlich.");
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.PreCommand)) await ExecAsync(container.Id, settings.PreCommand, cancellationToken);
            return new("", Models.BackupConsistencyMode.DockerExec, [], container.Id);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(settings.PostCommand))
            {
                try { await ExecAsync(container.Id, settings.PostCommand, CancellationToken.None); }
                catch (Exception cleanupException) { _logger.LogError(cleanupException, "Docker post hook rollback failed for container {ContainerId}", container.Id); }
            }
            throw;
        }
    }

    private async Task ExecAsync(string containerId, string command, CancellationToken cancellationToken)
    {
        using var client = CreateDockerClient();
        var createdResponse = await client.PostAsJsonAsync($"/v1.41/containers/{Uri.EscapeDataString(containerId)}/exec", new
        {
            AttachStdout = true,
            AttachStderr = true,
            Tty = true,
            Cmd = new[] { "/bin/sh", "-c", command }
        }, cancellationToken);
        createdResponse.EnsureSuccessStatusCode();
        var created = await createdResponse.Content.ReadFromJsonAsync<DockerExecCreate>(cancellationToken: cancellationToken)
            ?? throw new IOException("Docker lieferte keine Exec-ID.");
        using var started = await client.PostAsJsonAsync($"/v1.41/exec/{created.Id}/start", new { Detach = false, Tty = true }, cancellationToken);
        var output = await started.Content.ReadAsStringAsync(cancellationToken);
        started.EnsureSuccessStatusCode();
        var inspected = await client.GetFromJsonAsync<DockerExecInspect>($"/v1.41/exec/{created.Id}/json", cancellationToken)
            ?? throw new IOException("Docker lieferte keinen Exec-Status.");
        if (inspected.ExitCode != 0) throw new InvalidOperationException($"Docker Hook endete mit Exit-Code {inspected.ExitCode}: {Limit(output)}");
    }

    private async Task UnpauseAllAsync(IReadOnlyCollection<string> containerIds, CancellationToken cancellationToken)
    {
        if (containerIds.Count == 0) return;
        using var client = CreateDockerClient();
        List<Exception>? errors = null;
        foreach (var id in containerIds.Reverse())
        {
            try
            {
                using var response = await client.PostAsync($"/v1.41/containers/{Uri.EscapeDataString(id)}/unpause", null, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception exception) { (errors ??= []).Add(exception); }
        }
        if (errors is not null) throw new AggregateException("Mindestens ein Docker-Container konnte nicht fortgesetzt werden.", errors);
    }

    private static async Task<DockerContainerInspect> InspectAsync(HttpClient client, string name, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"/v1.41/containers/{Uri.EscapeDataString(name)}/json", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException($"Docker-Container '{name}' wurde nicht gefunden.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DockerContainerInspect>(cancellationToken: cancellationToken)
            ?? throw new IOException($"Docker lieferte keinen Status für Container '{name}'.");
    }

    private static void EnsureNotSelf(DockerContainerInspect container)
    {
        var self = Environment.GetEnvironmentVariable("HOSTNAME") ?? "";
        if (!string.IsNullOrWhiteSpace(self) && container.Id.StartsWith(self, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MatBu kann den Container, in dem der aktive Worker läuft, nicht pausieren oder per Hook verändern.");
    }

    private static IReadOnlyList<string> ParseContainers(string value) => (value ?? "")
        .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void Persist(BackupConsistencySettings settings, DockerConsistencyLease lease)
    {
        lock (_leaseGate)
        {
            var entries = ReadPersistedLocked().ToList();
            entries.RemoveAll(item => item.Lease.Id == lease.Id);
            entries.Add(new PersistedLease(settings, lease, DateTimeOffset.UtcNow));
            WritePersistedLocked(entries);
        }
    }

    private void RemovePersisted(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_leaseGate)
        {
            var entries = ReadPersistedLocked().ToList();
            if (entries.RemoveAll(item => item.Lease.Id == id) == 0) return;
            WritePersistedLocked(entries);
        }
    }

    private IReadOnlyList<PersistedLease> ReadPersisted()
    {
        lock (_leaseGate) return ReadPersistedLocked();
    }

    private IReadOnlyList<PersistedLease> ReadPersistedLocked()
    {
        if (!File.Exists(_leasePath)) return [];
        try
        {
            var json = _protector.Unprotect(File.ReadAllText(_leasePath));
            return JsonSerializer.Deserialize<List<PersistedLease>>(json, LeaseJson) ?? [];
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Protected Docker consistency lease file could not be read");
            return [];
        }
    }

    private void WritePersistedLocked(IReadOnlyCollection<PersistedLease> entries)
    {
        if (entries.Count == 0)
        {
            try { File.Delete(_leasePath); } catch { }
            return;
        }
        var temporaryPath = _leasePath + ".tmp";
        File.WriteAllText(temporaryPath, _protector.Protect(JsonSerializer.Serialize(entries, LeaseJson)));
        File.Move(temporaryPath, _leasePath, overwrite: true);
    }

    private static HttpClient CreateDockerClient()
    {
        var socketPath = Environment.GetEnvironmentVariable("DOCKER_SOCKET_PATH") ?? "/var/run/docker.sock";
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var endpoint = new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath);
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, 0);
                await socket.ConnectAsync(endpoint, cancellationToken);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string Limit(string value) => string.IsNullOrWhiteSpace(value) ? "keine Ausgabe" : value.Trim().Length <= 500 ? value.Trim() : value.Trim()[..500];
    private sealed record DockerContainerInspect(string Id, string Name, DockerContainerState State);
    private sealed record DockerContainerState(bool Running, bool Paused);
    private sealed record DockerExecCreate(string Id);
    private sealed record DockerExecInspect(bool Running, long ExitCode);
    private sealed record PersistedLease(BackupConsistencySettings Settings, DockerConsistencyLease Lease, DateTimeOffset CreateDate);
}
