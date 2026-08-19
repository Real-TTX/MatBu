using System.Security.Cryptography;

namespace MatBu.Services;

public static class ArchiveIntegrity
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Die zu prüfende Archivdatei wurde nicht gefunden.", path);
        var fullPath = Path.GetFullPath(path);
        await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(input, cancellationToken);
        return Convert.ToHexStringLower(hash);
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

}
