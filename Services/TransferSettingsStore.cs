using System.Text.Json;

namespace MatBu.Services;

/// <summary>
/// User-tunable transfer/behaviour settings, edited in the UI (Settings → Transfer) and persisted as JSON in
/// the shared data volume so both the web and the separate transfer-worker process observe the same values.
/// Replaces the former MATBU_* tuning environment variables (which now only seed the initial defaults).
/// </summary>
public sealed class TransferSettings
{
    public bool SparseCacheEnabled { get; set; } = true;
    public long BacklogHighMiB { get; set; } = 512;
    public long BacklogLowMiB { get; set; } = 128;
    public double MinFreeSpaceGiB { get; set; }            // 0 = automatic (drive/20, clamped 512 MiB..5 GiB)
    public int CacheRetentionHours { get; set; } = 168;
    public int SecondaryIdleTimeoutSeconds { get; set; } = 120;
    public int SecondaryHeartbeatSeconds { get; set; } = 10;
    public int SecondaryBuildStallSeconds { get; set; } = 1800;
    public bool SmbStreamingEnabled { get; set; } = true;

    public TransferSettings Clamped() => new()
    {
        SparseCacheEnabled = SparseCacheEnabled,
        BacklogLowMiB = Math.Max(8, BacklogLowMiB),
        BacklogHighMiB = Math.Max(Math.Max(8, BacklogLowMiB) + 8, BacklogHighMiB),
        MinFreeSpaceGiB = Math.Max(0, MinFreeSpaceGiB),
        CacheRetentionHours = Math.Clamp(CacheRetentionHours, 1, 8760),
        SecondaryIdleTimeoutSeconds = Math.Clamp(SecondaryIdleTimeoutSeconds, 5, 3600),
        SecondaryHeartbeatSeconds = Math.Clamp(SecondaryHeartbeatSeconds, 2, 60),
        SecondaryBuildStallSeconds = Math.Clamp(SecondaryBuildStallSeconds, 120, 21600),
        SmbStreamingEnabled = SmbStreamingEnabled
    };

    /// <summary>Built-in defaults, seeded from the legacy MATBU_* env vars if those are still set.</summary>
    public static TransferSettings FromEnvironmentDefaults()
    {
        long EnvLong(string name, long fallback) =>
            long.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
        int EnvInt(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
        double EnvDouble(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
        // Retention was historically a *double* hours env; honour fractional seeds instead of collapsing to the default.
        int EnvHours(string name, int fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0
                ? Math.Max(1, (int)Math.Round(h)) : fallback;

        return new TransferSettings
        {
            SparseCacheEnabled = Environment.GetEnvironmentVariable("MATBU_TRANSFER_SPARSE_CACHE") != "0",
            BacklogHighMiB = EnvLong("MATBU_TRANSFER_BACKLOG_HIGH_MIB", 512),
            BacklogLowMiB = EnvLong("MATBU_TRANSFER_BACKLOG_LOW_MIB", 128),
            MinFreeSpaceGiB = EnvDouble("MATBU_MIN_FREE_SPACE_GIB", 0),
            CacheRetentionHours = EnvHours("MATBU_TRANSFER_CACHE_RETENTION_HOURS", 168),
            SecondaryIdleTimeoutSeconds = EnvInt("MATBU_SECONDARY_COMMAND_IDLE_TIMEOUT_SECONDS", 120),
            SecondaryHeartbeatSeconds = EnvInt("MATBU_SECONDARY_HEARTBEAT_SECONDS", 10),
            SecondaryBuildStallSeconds = EnvInt("MATBU_SECONDARY_BUILD_STALL_SECONDS", 1800),
            SmbStreamingEnabled = Environment.GetEnvironmentVariable("MATBU_SMB_STREAMING") != "0"
        }.Clamped();
    }
}

public sealed class TransferSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    // Cross-process freshness window: a change saved by the web process is picked up by the worker within this time.
    private static readonly long CacheTtlTicks = (long)(3.0 * System.Diagnostics.Stopwatch.Frequency);
    private readonly string _path;
    private readonly object _gate = new();
    private TransferSettings? _cached;
    private long _cachedTicks;

    public TransferSettingsStore(IHostEnvironment environment)
    {
        var directory = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "transfer-settings.json");
    }

    /// <summary>Current effective settings. Cheap: cached for a few seconds so hot paths and the separate
    /// worker process both stay near-current without re-reading the file every call.</summary>
    public TransferSettings Read()
    {
        lock (_gate)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_cached is not null && (now - _cachedTicks) < CacheTtlTicks)
                return _cached;
            _cached = Load();
            _cachedTicks = now;
            return _cached;
        }
    }

    public TransferSettings Save(TransferSettings input)
    {
        var clamped = input.Clamped();
        lock (_gate)
        {
            // Write to a temp file and move into place so the separate worker process never reads a half-written file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(clamped, JsonOptions));
            // Retry the atomic replace: on Windows the worker process may momentarily hold the destination open
            // for reading, which briefly blocks the rename with a sharing violation (harmless, resolves in ms).
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temp, _path, overwrite: true); break; }
                catch (IOException) when (attempt < 10) { Thread.Sleep(20); }
            }
            _cached = clamped;
            _cachedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        return clamped;
    }

    private TransferSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                // Open with FileShare.Delete so a concurrent Save() in the other process can rename over this file
                // (Windows) without hitting a sharing violation; this reader keeps the old content until it closes.
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return (JsonSerializer.Deserialize<TransferSettings>(reader.ReadToEnd(), JsonOptions) ?? TransferSettings.FromEnvironmentDefaults()).Clamped();
            }
        }
        catch { /* fall through to env/defaults on a corrupt or vanishing file */ }
        return TransferSettings.FromEnvironmentDefaults();
    }
}
