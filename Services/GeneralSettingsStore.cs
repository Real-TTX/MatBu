using System.Text.Json;

namespace MatBu.Services;

/// <summary>
/// Global application settings edited in the UI (Settings → Allgemein) and persisted as JSON in the shared data
/// volume so both the web and the transfer-worker process observe the same values. Currently just the schedule
/// time zone (formerly the MATBU_TIME_ZONE env var, which now only seeds the initial default).
/// </summary>
public sealed class GeneralSettings
{
    public string TimeZoneId { get; set; } = "Europe/Berlin";

    public static GeneralSettings FromEnvironmentDefaults() => new()
    {
        TimeZoneId = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MATBU_TIME_ZONE"))
            ? "Europe/Berlin"
            : Environment.GetEnvironmentVariable("MATBU_TIME_ZONE")!
    };
}

public sealed class GeneralSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly long CacheTtlTicks = (long)(3.0 * System.Diagnostics.Stopwatch.Frequency);
    private readonly string _path;
    private readonly object _gate = new();
    private GeneralSettings? _cached;
    private long _cachedTicks;

    public GeneralSettingsStore(IHostEnvironment environment)
    {
        var directory = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "general-settings.json");
    }

    public GeneralSettings Read()
    {
        lock (_gate)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_cached is not null && (now - _cachedTicks) < CacheTtlTicks) return _cached;
            _cached = Load();
            _cachedTicks = now;
            return _cached;
        }
    }

    public GeneralSettings Save(GeneralSettings input)
    {
        var normalized = new GeneralSettings { TimeZoneId = string.IsNullOrWhiteSpace(input.TimeZoneId) ? "Europe/Berlin" : input.TimeZoneId.Trim() };
        lock (_gate)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(normalized, JsonOptions));
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temp, _path, overwrite: true); break; }
                catch (IOException) when (attempt < 10) { Thread.Sleep(20); }
            }
            _cached = normalized;
            _cachedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        return normalized;
    }

    /// <summary>Resolve the configured schedule time zone, falling back to the machine local zone if unknown.</summary>
    public TimeZoneInfo ResolveTimeZone()
    {
        var id = Read().TimeZoneId;
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    private GeneralSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var loaded = JsonSerializer.Deserialize<GeneralSettings>(reader.ReadToEnd(), JsonOptions);
                // Guard an explicit null/blank TimeZoneId in the JSON (deserialization overrides the field initializer),
                // which would otherwise cache a null id and throw in ResolveTimeZone().
                return loaded is null || string.IsNullOrWhiteSpace(loaded.TimeZoneId) ? GeneralSettings.FromEnvironmentDefaults() : loaded;
            }
        }
        catch { /* fall through to env/defaults on a corrupt or vanishing file */ }
        return GeneralSettings.FromEnvironmentDefaults();
    }
}
