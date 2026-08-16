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

    [Theory]
    [InlineData("vm/100/1755302400", "vm", "100", 1755302400L)]
    [InlineData("/ct/203/1755302401/", "ct", "203", 1755302401L)]
    public void SnapshotPath_ParseAcceptsNativeGuestSnapshots(string value, string type, string id, long time)
    {
        var result = ProxmoxBackupServerSnapshotPath.Parse(value);

        Assert.Equal(type, result.BackupType);
        Assert.Equal(id, result.BackupId);
        Assert.Equal(time, result.BackupTime);
    }

    [Theory]
    [InlineData("host/server/1755302400")]
    [InlineData("vm/not-a-number/1755302400")]
    [InlineData("vm/100/not-a-time")]
    [InlineData("vm/100")]
    public void SnapshotPath_ParseRejectsUnsafePaths(string value)
    {
        Assert.Throws<FormatException>(() => ProxmoxBackupServerSnapshotPath.Parse(value));
    }

    [Fact]
    public void NativeRetention_UsesOnlyCatalogVerifiedSnapshots()
    {
        var manifest = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new ProxmoxNativeSnapshotResult("qemu", 100, "vm", "vm/100/1755302400", 42, DateTimeOffset.UtcNow, true)
        });

        Assert.Equal(new[] { "vm/100/1755302400" }, BackupRetentionService.ParseNativeSnapshotPaths(manifest));
    }

    [Fact]
    public void NativeRetention_RejectsUnverifiedSnapshots()
    {
        var manifest = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new ProxmoxNativeSnapshotResult("qemu", 100, "vm", "vm/100/1755302400", 0, DateTimeOffset.UtcNow)
        });

        Assert.Throws<InvalidDataException>(() => BackupRetentionService.ParseNativeSnapshotPaths(manifest));
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
