using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MatBu.Services;

public sealed record ProxmoxBackupServerLocation(Uri Endpoint, string Datastore, string PveStorage, string Namespace, bool VerifyTls)
{
    public static ProxmoxBackupServerLocation Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("PBS erwartet eine HTTP(S)-Adresse mit datastore und pveStorage.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : "", StringComparer.OrdinalIgnoreCase);
        query.TryGetValue("datastore", out var datastore);
        query.TryGetValue("pveStorage", out var pveStorage);
        query.TryGetValue("namespace", out var backupNamespace);
        query.TryGetValue("verifyTls", out var verifyTlsText);
        if (string.IsNullOrWhiteSpace(datastore) || string.IsNullOrWhiteSpace(pveStorage))
            throw new FormatException("PBS-Adresse unvollständig. Beispiel: https://pbs:8007/?datastore=backup&pveStorage=pbs-backup");
        return new ProxmoxBackupServerLocation(
            new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/"),
            datastore.Trim(), pveStorage.Trim(), backupNamespace?.Trim() ?? "",
            !bool.TryParse(verifyTlsText, out var verifyTls) || verifyTls);
    }
}

public sealed record ProxmoxBackupServerSnapshot(string BackupType, string BackupId, long BackupTime, long Size)
{
    public string Path => $"{BackupType}/{BackupId}/{BackupTime}";
    public DateTimeOffset CreateDate => DateTimeOffset.FromUnixTimeSeconds(BackupTime);
}

public sealed record ProxmoxBackupServerSnapshotPath(string BackupType, string BackupId, long BackupTime)
{
    public static ProxmoxBackupServerSnapshotPath Parse(string value)
    {
        var parts = (value ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] is not ("vm" or "ct") ||
            !int.TryParse(parts[1], out var guestId) || guestId <= 0 ||
            !long.TryParse(parts[2], out var backupTime) || backupTime <= 0)
            throw new FormatException($"Ungültiger PBS-Snapshotpfad '{value}'. Erwartet wird vm/<ID>/<Unixzeit> oder ct/<ID>/<Unixzeit>.");
        return new ProxmoxBackupServerSnapshotPath(parts[0], guestId.ToString(), backupTime);
    }
}

public sealed class ProxmoxBackupServerService
{
    public async Task<GatewayObjectTestResult> TestAsync(string location, string? tokenId, string? tokenSecret, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var settings = ProxmoxBackupServerLocation.Parse(location);
            using var client = CreateClient(settings, tokenId, tokenSecret);
            using var version = await GetDataAsync(client, "api2/json/version", cancellationToken);
            using var status = await GetDataAsync(client, $"api2/json/admin/datastore/{Uri.EscapeDataString(settings.Datastore)}/status", cancellationToken);
            return new GatewayObjectTestResult(true, $"PBS API und Datastore '{settings.Datastore}' sind erreichbar; PVE-Storage-ID: {settings.PveStorage}.", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or FormatException or UnauthorizedAccessException)
        {
            return new GatewayObjectTestResult(false, $"PBS-Verbindung fehlgeschlagen: {exception.Message}", stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task<ProxmoxBackupServerSnapshot?> FindLatestSnapshotAsync(
        string location, string? tokenId, string? tokenSecret, string backupType, int guestId,
        DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        var settings = ProxmoxBackupServerLocation.Parse(location);
        using var client = CreateClient(settings, tokenId, tokenSecret);
        var path = $"api2/json/admin/datastore/{Uri.EscapeDataString(settings.Datastore)}/snapshots?backup-type={Uri.EscapeDataString(backupType)}&backup-id={guestId}";
        if (!string.IsNullOrWhiteSpace(settings.Namespace)) path += $"&ns={Uri.EscapeDataString(settings.Namespace)}";
        using var document = await GetDataAsync(client, path, cancellationToken);
        return document.RootElement.GetProperty("data").EnumerateArray()
            .Select(ParseSnapshot)
            .Where(snapshot => snapshot is not null && snapshot.CreateDate >= notBefore)
            .OrderByDescending(snapshot => snapshot!.BackupTime)
            .FirstOrDefault();
    }

    public async Task ForgetSnapshotAsync(
        string location,
        string? tokenId,
        string? tokenSecret,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        var settings = ProxmoxBackupServerLocation.Parse(location);
        var snapshot = ProxmoxBackupServerSnapshotPath.Parse(snapshotPath);
        using var client = CreateClient(settings, tokenId, tokenSecret);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"api2/json/admin/datastore/{Uri.EscapeDataString(settings.Datastore)}/snapshots");
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("backup-type", snapshot.BackupType),
            new("backup-id", snapshot.BackupId),
            new("backup-time", snapshot.BackupTime.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrWhiteSpace(settings.Namespace)) parameters.Add(new("ns", settings.Namespace));
        request.Content = new FormUrlEncodedContent(parameters);
        using var response = await client.SendAsync(request, cancellationToken);
        // Retention may be retried after a connection loss. A snapshot removed by the
        // previous attempt already satisfies the requested end state.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"PBS konnte Snapshot '{snapshotPath}' nicht entfernen ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 300)]}");
        }
    }

    private static ProxmoxBackupServerSnapshot? ParseSnapshot(JsonElement item)
    {
        if (!item.TryGetProperty("backup-type", out var type) || !item.TryGetProperty("backup-id", out var id) || !item.TryGetProperty("backup-time", out var time)) return null;
        var backupType = type.GetString();
        var backupId = id.GetString();
        if (string.IsNullOrWhiteSpace(backupType) || string.IsNullOrWhiteSpace(backupId) || !time.TryGetInt64(out var backupTime)) return null;
        var size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
        return new ProxmoxBackupServerSnapshot(backupType, backupId, backupTime, size);
    }

    private static HttpClient CreateClient(ProxmoxBackupServerLocation settings, string? tokenId, string? tokenSecret)
    {
        if (string.IsNullOrWhiteSpace(tokenId) || string.IsNullOrWhiteSpace(tokenSecret)) throw new UnauthorizedAccessException("PBS API Token-ID und Token-Secret fehlen.");
        var handler = new HttpClientHandler();
        if (!settings.VerifyTls) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var client = new HttpClient(handler) { BaseAddress = settings.Endpoint, Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("PBSAPIToken", $"{tokenId.Trim()}:{tokenSecret}");
        return client;
    }

    private static async Task<JsonDocument> GetDataAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"PBS API {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        }
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }
}
