using MatBu.Models;
using MatBu.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatBu.Tests;

public sealed class ArchiveIncrementalShaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"matbu-archive-{Guid.NewGuid():N}");
    private readonly string _source;
    private readonly string _output;

    public ArchiveIncrementalShaTests()
    {
        _source = Path.Combine(_root, "src");
        _output = Path.Combine(_root, "out.tar");
        Directory.CreateDirectory(_source);
        File.WriteAllBytes(Path.Combine(_source, "a.bin"), RandomBytes(2 * 1024 * 1024));
        File.WriteAllBytes(Path.Combine(_source, "b.txt"), RandomBytes(37_000));
    }

    [Theory]
    [InlineData(BackupCompression.None)]
    [InlineData(BackupCompression.Balanced)]
    public async Task CreateCompressed_IncrementalSha_MatchesFileHash(BackupCompression compression)
    {
        var archive = new ArchiveService(
            new FakeEnvironment(_root),
            new SmbClientService(NullLogger<SmbClientService>.Instance),
            new ProxmoxService(NullLogger<ProxmoxService>.Instance),
            NullLogger<ArchiveService>.Instance);

        var source = new BackupObject { Kind = ObjectKind.LocalFolder, Location = _source };

        var result = await archive.CreateCompressedAsync(source, null, _output, compression, null, CancellationToken.None);
        var fileHash = await ArchiveIntegrity.ComputeSha256Async(_output, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(result.Sha256));
        Assert.Equal(fileHash, result.Sha256);
    }

    [Fact]
    public async Task CreateCompressed_ParallelEstimate_ResolvesToExactTotalAtCompletion()
    {
        var archive = new ArchiveService(
            new FakeEnvironment(_root),
            new SmbClientService(NullLogger<SmbClientService>.Instance),
            new ProxmoxService(NullLogger<ProxmoxService>.Instance),
            NullLogger<ArchiveService>.Instance);

        var source = new BackupObject { Kind = ObjectKind.LocalFolder, Location = _source };
        var progresses = new List<ArchiveProgress>();

        await archive.CreateCompressedAsync(source, null, _output, BackupCompression.None, p => progresses.Add(p), CancellationToken.None);

        Assert.NotEmpty(progresses);
        var final = progresses[^1];
        // At completion the total is exact and non-zero (so the UI resolves to 100%), even though the
        // estimate runs in parallel and may start at 0.
        Assert.True(final.EstimatedSourceBytes > 0);
        Assert.Equal(final.SourceBytes, final.EstimatedSourceBytes);
        Assert.Equal(final.StoredBytes, final.EstimatedStoredBytes);
    }

    private static byte[] RandomBytes(int count)
    {
        var data = new byte[count];
        Random.Shared.NextBytes(data);
        return data;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeEnvironment(string contentRoot) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "MatBu.Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
