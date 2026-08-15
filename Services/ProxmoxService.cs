using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using MatBu.Models;

namespace MatBu.Services;

public sealed record ProxmoxLocation(Uri Endpoint, string Node, string Storage, string ExportPath, bool VerifyTls)
{
    public static ProxmoxLocation Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("Proxmox erwartet eine HTTP(S)-Adresse mit node, storage und path.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : "", StringComparer.OrdinalIgnoreCase);
        query.TryGetValue("node", out var node);
        query.TryGetValue("storage", out var storage);
        query.TryGetValue("path", out var path);
        query.TryGetValue("verifyTls", out var verifyTlsText);
        var hasStorage = !string.IsNullOrWhiteSpace(storage);
        var hasPath = !string.IsNullOrWhiteSpace(path);
        var isAbsolutePath = hasPath && (path!.StartsWith('/') || Path.IsPathFullyQualified(path!));
        if (string.IsNullOrWhiteSpace(node) || hasStorage != hasPath || hasPath && !isAbsolutePath)
            throw new FormatException("Proxmox-Adresse unvollständig. Native PBS benötigt node; Datei-Backups zusätzlich storage und einen absoluten path.");
        var endpoint = new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/");
        return new ProxmoxLocation(endpoint, node!.Trim(), storage?.Trim() ?? "", path?.Trim() ?? "", !bool.TryParse(verifyTlsText, out var verifyTls) || verifyTls);
    }
}

public sealed record ProxmoxGuest(string Type, int Id, string Name, string Node, string Status)
{
    public string SelectionPath => $"{Type}/{Id}";
}

public sealed record ProxmoxNativeGuestBackup(string GuestType, int GuestId, string GuestName, DateTimeOffset StartedDate, DateTimeOffset CompletedDate);

public sealed class ProxmoxService(ILogger<ProxmoxService> logger)
{
    public async Task<GatewayObjectTestResult> TestAsync(string location, string? tokenId, string? tokenSecret, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var settings = ProxmoxLocation.Parse(location);
            if (!string.IsNullOrWhiteSpace(settings.ExportPath) && !Directory.Exists(settings.ExportPath))
                throw new DirectoryNotFoundException($"Proxmox dump path is not mounted on this MatBu instance: {settings.ExportPath}");
            using var client = CreateClient(settings, tokenId, tokenSecret);
            using var version = await GetDataAsync(client, "api2/json/version", cancellationToken);
            var guests = await ListGuestsAsync(client, cancellationToken);
            return new GatewayObjectTestResult(true, $"Proxmox API erreichbar · {guests.Count} VM/CT gefunden · Node {settings.Node}.", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or FormatException or UnauthorizedAccessException or IOException)
        {
            return new GatewayObjectTestResult(false, $"Proxmox-Verbindung fehlgeschlagen: {exception.Message}", stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task<SourceBrowseResult> BrowseGuestsAsync(string location, string? path, string? tokenId, string? tokenSecret, CancellationToken cancellationToken)
    {
        var normalized = SourceSelection.Normalize(string.IsNullOrWhiteSpace(path) ? [] : [path]).FirstOrDefault() ?? "";
        if (!string.IsNullOrEmpty(normalized)) return new SourceBrowseResult(normalized, []);
        var settings = ProxmoxLocation.Parse(location);
        using var client = CreateClient(settings, tokenId, tokenSecret);
        var guests = await ListGuestsAsync(client, cancellationToken);
        return new SourceBrowseResult("", guests.Where(guest => guest.Node.Equals(settings.Node, StringComparison.OrdinalIgnoreCase)).Select(guest =>
            new SourceBrowseEntry($"{guest.Id} · {guest.Name} · {guest.Type.ToUpperInvariant()} · {guest.Status}", guest.SelectionPath, false)).ToList());
    }

    public async Task<IReadOnlyList<string>> CreateBackupFilesAsync(string location, string? tokenId, string? tokenSecret, IReadOnlyList<string> selectedGuests, CancellationToken cancellationToken)
    {
        var settings = ProxmoxLocation.Parse(location);
        if (string.IsNullOrWhiteSpace(settings.Storage) || string.IsNullOrWhiteSpace(settings.ExportPath))
            throw new InvalidOperationException("Für ein Proxmox-Dateibackup müssen storage und der gemountete path konfiguriert sein.");
        if (!Directory.Exists(settings.ExportPath))
            throw new DirectoryNotFoundException($"Der Proxmox-Dump-Pfad ist auf dieser MatBu-Instanz nicht gemountet: {settings.ExportPath}");
        using var client = CreateClient(settings, tokenId, tokenSecret);
        var available = await ListGuestsAsync(client, cancellationToken);
        var selected = selectedGuests.Count == 0
            ? available.Where(guest => guest.Node.Equals(settings.Node, StringComparison.OrdinalIgnoreCase)).ToList()
            : available.Where(guest => selectedGuests.Contains(guest.SelectionPath, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Keine passende Proxmox-VM oder kein Container wurde ausgewählt.");

        var files = new List<string>();
        foreach (var guest in selected)
        {
            if (!guest.Node.Equals(settings.Node, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Guest {guest.Id} läuft auf Node '{guest.Node}', das Object ist aber für '{settings.Node}' konfiguriert.");
            var started = DateTimeOffset.UtcNow.AddMinutes(-1);
            using var response = await client.PostAsync($"api2/json/nodes/{Uri.EscapeDataString(settings.Node)}/vzdump", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["vmid"] = guest.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["storage"] = settings.Storage, ["mode"] = "snapshot", ["compress"] = "0"
            }), cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var upid = document.RootElement.GetProperty("data").GetString() ?? throw new InvalidDataException("Proxmox lieferte keine Task-ID.");
            await WaitForTaskAsync(client, settings.Node, upid, cancellationToken);
            var prefix = $"vzdump-{guest.Type}-{guest.Id}-";
            var file = Directory.EnumerateFiles(settings.ExportPath, prefix + "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path)).Where(info => info.LastWriteTimeUtc >= started.UtcDateTime)
                .OrderByDescending(info => info.LastWriteTimeUtc).FirstOrDefault()
                ?? throw new FileNotFoundException($"Proxmox meldete Erfolg, aber im gemounteten Dump-Pfad fehlt {prefix}*.");
            files.Add(file.FullName);
            logger.LogInformation("Proxmox vzdump completed for {Type}/{GuestId}: {File}", guest.Type, guest.Id, file.Name);
        }
        return files;
    }

    public async Task<IReadOnlyList<ProxmoxNativeGuestBackup>> CreateNativePbsBackupsAsync(
        string location,
        string? tokenId,
        string? tokenSecret,
        IReadOnlyList<string> selectedGuests,
        string pveStorage,
        Func<CancellationToken, Task>? heartbeat,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pveStorage)) throw new ArgumentException("Die PVE-Storage-ID für PBS fehlt.", nameof(pveStorage));
        var settings = ProxmoxLocation.Parse(location);
        using var client = CreateClient(settings, tokenId, tokenSecret);
        var available = await ListGuestsAsync(client, cancellationToken);
        var selected = selectedGuests.Count == 0
            ? available.Where(guest => guest.Node.Equals(settings.Node, StringComparison.OrdinalIgnoreCase)).ToList()
            : available.Where(guest => selectedGuests.Contains(guest.SelectionPath, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Keine passende Proxmox-VM oder kein Container wurde ausgewählt.");

        var results = new List<ProxmoxNativeGuestBackup>();
        foreach (var guest in selected)
        {
            if (!guest.Node.Equals(settings.Node, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Guest {guest.Id} läuft auf Node '{guest.Node}', erwartet wird '{settings.Node}'.");
            var started = DateTimeOffset.UtcNow;
            using var response = await client.PostAsync($"api2/json/nodes/{Uri.EscapeDataString(settings.Node)}/vzdump", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["vmid"] = guest.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["storage"] = pveStorage.Trim(),
                ["mode"] = "snapshot"
            }), cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var upid = document.RootElement.GetProperty("data").GetString() ?? throw new InvalidDataException("Proxmox lieferte keine Task-ID.");
            await WaitForTaskAsync(client, settings.Node, upid, heartbeat, cancellationToken);
            results.Add(new ProxmoxNativeGuestBackup(guest.Type, guest.Id, guest.Name, started, DateTimeOffset.UtcNow));
        }
        return results;
    }

    public static void CleanupBackupFiles(IEnumerable<string> files)
    {
        foreach (var file in files) try { File.Delete(file); } catch { }
    }

    private static async Task<IReadOnlyList<ProxmoxGuest>> ListGuestsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var document = await GetDataAsync(client, "api2/json/cluster/resources?type=vm", cancellationToken);
        return document.RootElement.GetProperty("data").EnumerateArray()
            .Where(item => item.TryGetProperty("vmid", out _) && item.TryGetProperty("type", out var type) && type.GetString() is "qemu" or "lxc")
            .Select(item => new ProxmoxGuest(item.GetProperty("type").GetString()!, item.GetProperty("vmid").GetInt32(),
                item.TryGetProperty("name", out var name) ? name.GetString() ?? "Ohne Name" : "Ohne Name",
                item.TryGetProperty("node", out var node) ? node.GetString() ?? "" : "",
                item.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown"))
            .OrderBy(guest => guest.Id).ToList();
    }

    private static Task WaitForTaskAsync(HttpClient client, string node, string upid, CancellationToken cancellationToken) =>
        WaitForTaskAsync(client, node, upid, null, cancellationToken);

    private static async Task WaitForTaskAsync(HttpClient client, string node, string upid, Func<CancellationToken, Task>? heartbeat, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var document = await GetDataAsync(client, $"api2/json/nodes/{Uri.EscapeDataString(node)}/tasks/{Uri.EscapeDataString(upid)}/status", cancellationToken);
            var data = document.RootElement.GetProperty("data");
            if (data.TryGetProperty("status", out var status) && status.GetString() == "stopped")
            {
                var exit = data.TryGetProperty("exitstatus", out var exitStatus) ? exitStatus.GetString() : null;
                if (!string.Equals(exit, "OK", StringComparison.OrdinalIgnoreCase)) throw new IOException($"Proxmox-vzdump endete mit '{exit ?? "unbekannt"}'.");
                return;
            }
            if (heartbeat is not null) await heartbeat(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static HttpClient CreateClient(ProxmoxLocation settings, string? tokenId, string? tokenSecret)
    {
        if (string.IsNullOrWhiteSpace(tokenId) || string.IsNullOrWhiteSpace(tokenSecret)) throw new UnauthorizedAccessException("Proxmox API Token-ID und Token-Secret fehlen.");
        var handler = new HttpClientHandler();
        if (!settings.VerifyTls) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var client = new HttpClient(handler) { BaseAddress = settings.Endpoint, Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("PVEAPIToken", $"{tokenId.Trim()}={tokenSecret}");
        return client;
    }

    private static async Task<JsonDocument> GetDataAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Proxmox API {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
    }
}
