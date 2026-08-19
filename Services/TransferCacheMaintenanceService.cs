namespace MatBu.Services;

public sealed record TransferCacheCleanupResult(int DeletedFiles, long ReclaimedBytes);

public sealed class TransferCacheMaintenanceService(
    ArchiveService archiveService,
    ILogger<TransferCacheMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Cleanup();
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken)) Cleanup();
    }

    private void Cleanup()
    {
        try
        {
            var retention = ResolveRetention();
            var result = CleanupExpiredFiles(archiveService.CacheDirectory, DateTime.UtcNow.Subtract(retention));
            if (result.DeletedFiles > 0)
                logger.LogInformation(
                    "Removed {Files} expired transfer cache files and reclaimed {Bytes} bytes",
                    result.DeletedFiles,
                    result.ReclaimedBytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Expired transfer cache cleanup failed");
        }
    }

    public static TransferCacheCleanupResult CleanupExpiredFiles(string cacheDirectory, DateTime cutoffUtc)
    {
        if (!Directory.Exists(cacheDirectory)) return new TransferCacheCleanupResult(0, 0);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory)) + Path.DirectorySeparatorChar;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var deleted = 0;
        long reclaimed = 0;
        foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*", options))
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            try
            {
                var info = new FileInfo(fullPath);
                if (info.LastWriteTimeUtc >= cutoffUtc) continue;
                var length = info.Length;
                info.Delete();
                reclaimed += length;
                deleted++;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        foreach (var directory in Directory.EnumerateDirectories(cacheDirectory, "*", options).OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new TransferCacheCleanupResult(deleted, reclaimed);
    }

    private static TimeSpan ResolveRetention()
    {
        var configured = Environment.GetEnvironmentVariable("MATBU_TRANSFER_CACHE_RETENTION_HOURS");
        return double.TryParse(configured, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0
            ? TimeSpan.FromHours(Math.Max(1, hours))
            : TimeSpan.FromDays(7);
    }
}
