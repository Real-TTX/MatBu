using MatBu.Services;

namespace MatBu.Tests;

public sealed class ArchiveIntegrityTests
{
    [Fact]
    public async Task VerifySha256_AcceptsUnchangedFileAndRejectsCorruption()
    {
        var path = Path.Combine(Path.GetTempPath(), $"matbu-integrity-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
            var hash = await ArchiveIntegrity.ComputeSha256Async(path, CancellationToken.None);

            Assert.True(ArchiveIntegrity.IsSha256(hash));
            await ArchiveIntegrity.VerifySha256Async(path, hash, CancellationToken.None);

            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 6]);
            await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveIntegrity.VerifySha256Async(path, hash, CancellationToken.None));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
