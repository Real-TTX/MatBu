using MatBu.Models;
using MatBu.Services;

namespace MatBu.Tests;

public sealed class ProxmoxBackupServerTests
{
    [Fact]
    public void Parse_ReadsDatastorePveStorageAndNamespace()
    {
        var result = ProxmoxBackupServerLocation.Parse("https://pbs.example:8007/?datastore=main&pveStorage=pbs-main&namespace=customers%2Facme&verifyTls=false");

        Assert.Equal("https://pbs.example:8007/", result.Endpoint.ToString());
        Assert.Equal("main", result.Datastore);
        Assert.Equal("pbs-main", result.PveStorage);
        Assert.Equal("customers/acme", result.Namespace);
        Assert.False(result.VerifyTls);
    }

    [Theory]
    [InlineData("https://pbs.example:8007/?datastore=main")]
    [InlineData("https://pbs.example:8007/?pveStorage=pbs-main")]
    [InlineData("not-a-url")]
    public void Parse_RejectsIncompleteLocations(string value)
    {
        Assert.Throws<FormatException>(() => ProxmoxBackupServerLocation.Parse(value));
    }

    [Fact]
    public void RoutePolicy_AcceptsNativeRouteOnSameInstance()
    {
        var task = new BackupTask { Method = BackupMethod.ProxmoxNative };
        var source = new BackupObject { Kind = ObjectKind.Proxmox, InstanceId = 2 };
        var target = new BackupObject { Kind = ObjectKind.ProxmoxBackupServer, InstanceId = 2 };

        Assert.Null(BackupRoutePolicy.Validate(task, source, target));
    }

    [Fact]
    public void RoutePolicy_RejectsNativeRouteAcrossInstances()
    {
        var task = new BackupTask { Method = BackupMethod.ProxmoxNative };
        var source = new BackupObject { Kind = ObjectKind.Proxmox, InstanceId = 1 };
        var target = new BackupObject { Kind = ObjectKind.ProxmoxBackupServer, InstanceId = 2 };

        Assert.NotNull(BackupRoutePolicy.Validate(task, source, target));
    }
}
