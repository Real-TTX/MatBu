using MatBu.Models;
using MatBu.Services;

namespace MatBu.Tests;

public sealed class BackupRoutePolicyTests
{
    [Fact]
    public void RejectsDockerVolumeWrittenBackToSameVolume()
    {
        var task = new BackupTask { Method = BackupMethod.Full };
        var source = new BackupObject { Kind = ObjectKind.DockerVolume, Location = "customer-data", InstanceId = 1 };
        var target = new BackupObject { Kind = ObjectKind.DockerVolume, Location = "CUSTOMER-DATA", InstanceId = 1 };

        Assert.NotNull(BackupRoutePolicy.Validate(task, source, target));
    }

    [Fact]
    public void RejectsLocalTargetNestedInsideLocalSource()
    {
        var task = new BackupTask { Method = BackupMethod.Full };
        var source = new BackupObject { Kind = ObjectKind.LocalFolder, Location = Path.Combine(Path.GetTempPath(), "source"), InstanceId = 1 };
        var target = new BackupObject { Kind = ObjectKind.LocalFolder, Location = Path.Combine(Path.GetTempPath(), "source", "backups"), InstanceId = 1 };

        Assert.NotNull(BackupRoutePolicy.Validate(task, source, target));
    }

    [Fact]
    public void AllowsIdenticallyNamedDockerVolumesOnDifferentInstances()
    {
        var task = new BackupTask { Method = BackupMethod.Full };
        var source = new BackupObject { Kind = ObjectKind.DockerVolume, Location = "customer-data", InstanceId = 1 };
        var target = new BackupObject { Kind = ObjectKind.DockerVolume, Location = "customer-data", InstanceId = 2 };

        Assert.Null(BackupRoutePolicy.Validate(task, source, target));
    }
}
