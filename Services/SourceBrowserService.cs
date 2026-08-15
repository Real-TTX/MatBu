using MatBu.Models;

namespace MatBu.Services;

public sealed record SourceBrowseRequest(ObjectKind Kind, string Location, string Path, string? SmbUsername, string? SmbPassword);
public sealed record SourceBrowseEntry(string Name, string Path, bool HasChildren = true);
public sealed record SourceBrowseResult(string Path, IReadOnlyList<SourceBrowseEntry> Entries);

public sealed class SourceBrowserService(SmbClientService smbClient, ArchiveService archiveService, ProxmoxService proxmox)
{
    public async Task<SourceBrowseResult> BrowseAsync(
        BackupObject source,
        string? path,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        if (source.Kind == ObjectKind.Proxmox)
            return await proxmox.BrowseGuestsAsync(source.Location, path, credential?.Username, credential?.Password, cancellationToken);

        var normalized = SourceSelection.Normalize(string.IsNullOrWhiteSpace(path) ? [] : [path]).FirstOrDefault() ?? "";
        IReadOnlyList<string> names = source.Kind switch
        {
            ObjectKind.LocalFolder => BrowseLocal(source.Location, normalized),
            ObjectKind.Smb => await smbClient.ListDirectoriesAsync(source.Location, normalized, credential, cancellationToken),
            ObjectKind.DockerVolume => await archiveService.BrowseDockerVolumeDirectoriesAsync(source.Location, normalized, cancellationToken),
            _ => throw new InvalidOperationException($"Die Ordnerauswahl wird für den Quelltyp '{source.Kind}' noch nicht unterstützt.")
        };
        var entries = names.Select(name => new SourceBrowseEntry(name, string.IsNullOrEmpty(normalized) ? name : $"{normalized}/{name}")).ToList();
        return new SourceBrowseResult(normalized, entries);
    }

    private static IReadOnlyList<string> BrowseLocal(string rootPath, string relativePath)
    {
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException($"Quellordner wurde nicht gefunden: {rootPath}");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = string.IsNullOrEmpty(relativePath)
            ? root
            : Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (candidate != root && !candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Der ausgewählte Pfad liegt außerhalb des Quell-Objects.");
        if (!Directory.Exists(candidate)) throw new DirectoryNotFoundException($"Quellordner wurde nicht gefunden: {relativePath}");
        return Directory.EnumerateDirectories(candidate)
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
