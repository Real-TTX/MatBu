using MatBu.Models;

namespace MatBu.Services;

public static class BackupMethodPolicy
{
    public static bool IsChunked(BackupMethod method) => method is
        BackupMethod.ForwardIncremental or BackupMethod.Differential or BackupMethod.ReverseIncremental;

    public static string Label(BackupMethod method) => method switch
    {
        BackupMethod.ForwardIncremental => "Forward Incremental",
        BackupMethod.Differential => "Differential",
        BackupMethod.ReverseIncremental => "Reverse Incremental",
        _ => "Full"
    };
}
