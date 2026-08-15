using System.Diagnostics;
using MatBu.Models;

namespace MatBu.Services;

public sealed class ObjectConnectivityTester(SmbClientService smbClient)
{
    public async Task<GatewayObjectTestResult> TestAsync(
        BackupObject item,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        if (item.Kind == ObjectKind.LocalFolder)
        {
            var success = Directory.Exists(item.Location);
            return new GatewayObjectTestResult(
                success,
                success ? "Lokaler Pfad ist erreichbar." : "Lokaler Pfad wurde nicht gefunden.",
                started.ElapsedMilliseconds);
        }

        if (item.Kind == ObjectKind.DockerVolume)
            return new GatewayObjectTestResult(true, "Docker-Volume wird durch den lokalen Worker geprüft.", started.ElapsedMilliseconds);

        if (item.Kind != ObjectKind.Smb)
            return new GatewayObjectTestResult(false, "Dieser Object-Typ unterstützt keinen lokalen Verbindungstest.", started.ElapsedMilliseconds);

        (string Username, string Password)? credential = string.IsNullOrWhiteSpace(username) || password is null
            ? null
            : (username.Trim(), password);
        return await smbClient.TestAsync(item.Location, item.Direction, credential, cancellationToken);
    }
}
