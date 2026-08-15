namespace MatBu.Services;

public sealed record ProxmoxNativeBackupRequest(
    string SourceLocation,
    string? SourceTokenId,
    string? SourceTokenSecret,
    string TargetLocation,
    string? TargetTokenId,
    string? TargetTokenSecret,
    IReadOnlyList<string> SelectedGuests,
    long JobId);

public sealed record ProxmoxNativeSnapshotResult(
    string GuestType,
    int GuestId,
    string GuestName,
    string SnapshotPath,
    long Size,
    DateTimeOffset CreateDate);

public sealed record ProxmoxNativeBackupResult(
    IReadOnlyList<ProxmoxNativeSnapshotResult> Snapshots,
    long TotalBytes,
    string Destination);

public sealed class ProxmoxNativeBackupService(
    ProxmoxService proxmox,
    ProxmoxBackupServerService pbs)
{
    public async Task<ProxmoxNativeBackupResult> ExecuteAsync(
        ProxmoxNativeBackupRequest request,
        Func<CancellationToken, Task>? heartbeat,
        CancellationToken cancellationToken)
    {
        var pbsSettings = ProxmoxBackupServerLocation.Parse(request.TargetLocation);
        var guests = await proxmox.CreateNativePbsBackupsAsync(
            request.SourceLocation,
            request.SourceTokenId,
            request.SourceTokenSecret,
            request.SelectedGuests,
            pbsSettings.PveStorage,
            heartbeat,
            cancellationToken);

        var snapshots = new List<ProxmoxNativeSnapshotResult>();
        foreach (var guest in guests)
        {
            var backupType = guest.GuestType.Equals("qemu", StringComparison.OrdinalIgnoreCase) ? "vm" : "ct";
            var snapshot = await FindSnapshotWithRetryAsync(request, backupType, guest, heartbeat, cancellationToken);
            snapshots.Add(new ProxmoxNativeSnapshotResult(
                guest.GuestType,
                guest.GuestId,
                guest.GuestName,
                snapshot?.Path ?? $"{backupType}/{guest.GuestId}/{guest.CompletedDate.ToUnixTimeSeconds()}",
                snapshot?.Size ?? 0,
                snapshot?.CreateDate ?? guest.CompletedDate));
        }

        var ns = string.IsNullOrWhiteSpace(pbsSettings.Namespace) ? "" : $"/{pbsSettings.Namespace.Trim('/')}";
        var destination = $"pbs://{pbsSettings.Endpoint.Host}/{pbsSettings.Datastore}{ns}";
        return new ProxmoxNativeBackupResult(snapshots, snapshots.Sum(snapshot => snapshot.Size), destination);
    }

    private async Task<ProxmoxBackupServerSnapshot?> FindSnapshotWithRetryAsync(
        ProxmoxNativeBackupRequest request,
        string backupType,
        ProxmoxNativeGuestBackup guest,
        Func<CancellationToken, Task>? heartbeat,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var snapshot = await pbs.FindLatestSnapshotAsync(
                request.TargetLocation,
                request.TargetTokenId,
                request.TargetTokenSecret,
                backupType,
                guest.GuestId,
                guest.StartedDate.AddMinutes(-1),
                cancellationToken);
            if (snapshot is not null) return snapshot;
            if (heartbeat is not null) await heartbeat(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        return null;
    }
}
