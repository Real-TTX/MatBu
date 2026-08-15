using MatBu.Services;

namespace MatBu.Tests;

public sealed class IncrementalTransitionTests
{
    [Fact]
    public void MarkChunksNeededForTransition_MarksBaselineReversionForTransfer()
    {
        var baselineHash = new string('a', 64);
        var previousHash = new string('b', 64);
        var manifest = Manifest(baselineHash, changed: false);
        var previous = Manifest(previousHash, changed: true);
        IncrementalSourceService.MarkChunksNeededForTransition(manifest, previous);

        Assert.True(manifest.Files[0].Chunks[0].Changed);
    }

    [Fact]
    public void MarkChunksNeededForTransition_ReusesChunkAlreadyInPlainCurrent()
    {
        var hash = new string('a', 64);
        var manifest = Manifest(hash, changed: false);
        var previous = Manifest(hash, changed: true);
        IncrementalSourceService.MarkChunksNeededForTransition(manifest, previous);

        Assert.False(manifest.Files[0].Chunks[0].Changed);
    }

    private static IncrementalBackupManifest Manifest(string hash, bool changed) => new()
    {
        ChunkSizeBytes = 4 * 1024 * 1024,
        Files =
        [
            new IncrementalFileManifest
            {
                RelativePath = "vm.vma",
                Length = 1024,
                ContentHash = hash,
                Chunks = [new IncrementalChunkManifest { Sequence = 0, Offset = 0, Length = 1024, Hash = hash, Changed = changed }]
            }
        ]
    };
}
