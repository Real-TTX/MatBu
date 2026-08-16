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
        if (source.InstanceId == target.InstanceId && IsSameStorage(source, target))
            return "Quelle und Ziel liegen im selben Speicherbereich. Das würde das Backup in seine eigene Quelle schreiben.";
        return null;
    }

    private static bool IsSameStorage(BackupObject source, BackupObject target)
    {
        if (source.Kind == ObjectKind.DockerVolume && target.Kind == ObjectKind.DockerVolume)
            return source.Location.Trim().Equals(target.Location.Trim(), StringComparison.OrdinalIgnoreCase);

        if (source.Kind == ObjectKind.LocalFolder && target.Kind == ObjectKind.LocalFolder)
        {
            var sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Location));
            var targetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Location));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return targetPath.Equals(sourcePath, comparison) || targetPath.StartsWith(sourcePath + Path.DirectorySeparatorChar, comparison);
        }

        if (source.Kind == ObjectKind.Smb && target.Kind == ObjectKind.Smb)
        {
            var sourcePath = source.Location.Replace('/', '\\').TrimEnd('\\');
            var targetPath = target.Location.Replace('/', '\\').TrimEnd('\\');
            return targetPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) ||
                   targetPath.StartsWith(sourcePath + "\\", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
