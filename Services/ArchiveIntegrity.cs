using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace MatBu.Services;

public static class ArchiveIntegrity
{
    private static readonly ConcurrentDictionary<string, CachedHash> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Die zu prüfende Archivdatei wurde nicht gefunden.", path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (Cache.TryGetValue(fullPath, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            return cached.Sha256;
        await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(input, cancellationToken);
        var sha256 = Convert.ToHexStringLower(hash);
        Cache[fullPath] = new CachedHash(info.Length, info.LastWriteTimeUtc, sha256);
        return sha256;
    }

    public static async Task VerifySha256Async(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        if (!IsSha256(expectedSha256)) throw new InvalidDataException("Die erwartete SHA-256-Prüfsumme ist ungültig.");
        var actual = await ComputeSha256Async(path, cancellationToken);
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Integritätsprüfung fehlgeschlagen: SHA-256 erwartet {expectedSha256.ToLowerInvariant()}, erhalten {actual}.");
    }

    public static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private sealed record CachedHash(long Length, DateTime LastWriteUtc, string Sha256);
}
