using MatBu.Models;
using MatBu.Services;

namespace MatBu.Tests;

public sealed class BackupMethodPolicyTests
{
    [Theory]
    [InlineData(BackupMethod.Full, false)]
    [InlineData(BackupMethod.ForwardIncremental, true)]
    [InlineData(BackupMethod.Differential, true)]
    [InlineData(BackupMethod.ReverseIncremental, true)]
    public void IsChunked_IdentifiesBlockBasedMethods(BackupMethod method, bool expected)
    {
        Assert.Equal(expected, BackupMethodPolicy.IsChunked(method));
    }

    [Theory]
    [InlineData(BackupMethod.Full, "Full")]
    [InlineData(BackupMethod.ForwardIncremental, "Forward Incremental")]
    [InlineData(BackupMethod.Differential, "Differential")]
    [InlineData(BackupMethod.ReverseIncremental, "Reverse Incremental")]
    public void Label_ReturnsUserFacingName(BackupMethod method, string expected)
    {
        Assert.Equal(expected, BackupMethodPolicy.Label(method));
    }
}
