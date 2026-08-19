using MatBu.Services;

namespace MatBu.Tests;

public sealed class SparseFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"matbu-sparse-{Guid.NewGuid():N}.bin");

    [Theory]
    [InlineData(0, 8192, 4096, 0, 8192)]     // already aligned
    [InlineData(100, 8192, 4096, 4096, 4096)] // start rounds up, end rounds down to whole blocks
    [InlineData(0, 4000, 4096, 0, 0)]         // sub-block range frees nothing
    [InlineData(5000, 5100, 4096, 0, 0)]      // range inside one block frees nothing
    public void AlignedRange_SnapsInwardToWholeBlocks(long from, long to, int block, long expOffset, long expLength)
    {
        var (offset, length) = SparseFile.AlignedRange(from, to, block);
        Assert.Equal(expOffset, offset);
        Assert.Equal(expLength, length);
    }

    [Fact]
    public void PunchHole_KeepsLogicalLengthAndZeroesReleasedRegion()
    {
        // 12 MiB of 0xAB, then punch the middle 4 MiB (block-aligned).
        const int total = 12 * 1024 * 1024;
        var data = new byte[total];
        Array.Fill(data, (byte)0xAB);
        File.WriteAllBytes(_path, data);

        var punched = SparseFile.TryPunchHole(_path, 4 * 1024 * 1024, 4 * 1024 * 1024);

        // Logical length is always preserved, whether or not the platform supports hole punching.
        Assert.Equal(total, new FileInfo(_path).Length);

        var readback = File.ReadAllBytes(_path);
        Assert.Equal((byte)0xAB, readback[0]);                       // region before hole intact
        Assert.Equal((byte)0xAB, readback[total - 1]);              // region after hole intact
        if (punched)
        {
            // A punched hole reads back as zeros.
            Assert.Equal(0, readback[6 * 1024 * 1024]);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
