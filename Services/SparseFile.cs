using System.Runtime.InteropServices;

namespace MatBu.Services;

/// <summary>
/// Releases the physical disk blocks of already-consumed regions of a spool file while keeping the file's
/// logical length intact (sparse "hole punching"). This lets the secondary stream a large source archive
/// through a bounded cache footprint: everything already uploaded is deallocated, so peak disk usage stays
/// close to the un-transferred backlog rather than the full archive size. Unsupported platforms/filesystems
/// fall back to a no-op, in which case behaviour is identical to a plain growing cache file.
/// </summary>
public static class SparseFile
{
    /// <summary>
    /// Snap the range [from, to) inward to whole filesystem blocks so only fully-covered blocks are punched
    /// (partial blocks cannot be freed and would corrupt still-needed bytes). Returns a zero length when the
    /// range does not cover a whole block yet.
    /// </summary>
    public static (long Offset, long Length) AlignedRange(long from, long to, int block = 4096)
    {
        if (block <= 0 || to <= from) return (0, 0);
        var start = ((from + block - 1) / block) * block; // round up
        var end = (to / block) * block;                    // round down
        return end > start ? (start, end - start) : (0, 0);
    }

    /// <summary>Punch a hole (deallocate blocks) in <paramref name="path"/>. Returns false if unsupported or it failed.</summary>
    public static bool TryPunchHole(string path, long offset, long length)
    {
        if (length <= 0 || offset < 0) return false;
        try
        {
            if (OperatingSystem.IsWindows()) return PunchWindows(path, offset, length);
            if (OperatingSystem.IsLinux()) return PunchLinux(path, offset, length);
        }
        catch
        {
            // Best effort: any failure simply leaves the blocks allocated.
        }
        return false;
    }

    // ---- Windows: FSCTL_SET_SPARSE + FSCTL_SET_ZERO_DATA ----

    private const uint FSCTL_SET_SPARSE = 0x000900C4;
    private const uint FSCTL_SET_ZERO_DATA = 0x000980C8;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileZeroDataInformation
    {
        public long FileOffset;
        public long BeyondFinalZero;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    private static bool PunchWindows(string path, long offset, long length)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        DeviceIoControl(handle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); // best effort
        var info = new FileZeroDataInformation { FileOffset = offset, BeyondFinalZero = offset + length };
        var size = Marshal.SizeOf<FileZeroDataInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            return DeviceIoControl(handle, FSCTL_SET_ZERO_DATA, buffer, (uint)size, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- Linux: fallocate(FALLOC_FL_PUNCH_HOLE | FALLOC_FL_KEEP_SIZE) ----

    private const int FALLOC_FL_KEEP_SIZE = 0x01;
    private const int FALLOC_FL_PUNCH_HOLE = 0x02;

    [DllImport("libc", SetLastError = true)]
    private static extern int fallocate(int fd, int mode, long offset, long len);

    private static bool PunchLinux(string path, long offset, long length)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var fd = handle.DangerousGetHandle().ToInt32();
        return fallocate(fd, FALLOC_FL_PUNCH_HOLE | FALLOC_FL_KEEP_SIZE, offset, length) == 0;
    }
}
