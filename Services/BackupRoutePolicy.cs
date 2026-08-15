using MatBu.Models;

namespace MatBu.Services;

public static class BackupRoutePolicy
{
    public static string? Validate(BackupTask task, BackupObject source, BackupObject target)
    {
        var nativeRoute = source.Kind == ObjectKind.Proxmox && target.Kind == ObjectKind.ProxmoxBackupServer;
        if (task.Method == BackupMethod.ProxmoxNative && !nativeRoute)
            return "Proxmox Native benötigt Proxmox VE als Quelle und Proxmox Backup Server als Ziel.";
        if (target.Kind == ObjectKind.ProxmoxBackupServer && task.Method != BackupMethod.ProxmoxNative)
            return "Ein PBS-Ziel muss mit Proxmox Native verwendet werden.";
        if (nativeRoute && source.InstanceId != target.InstanceId)
            return "PVE und PBS müssen derselben MatBu-Instanz zugeordnet sein.";
        return null;
    }
}
