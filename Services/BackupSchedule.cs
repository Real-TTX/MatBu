using System.Globalization;
using System.Text.RegularExpressions;

namespace MatBu.Services;

public enum BackupScheduleKind { IntervalMinutes, IntervalHours, Daily, Weekly }

public sealed record BackupScheduleDefinition(
    BackupScheduleKind Kind,
    int IntervalValue,
    TimeOnly Time,
    DayOfWeek DayOfWeek,
    IReadOnlyList<DayOfWeek>? DaysOfWeek = null)
{
    public IReadOnlyList<DayOfWeek> EffectiveDays => DaysOfWeek is { Count: > 0 }
        ? DaysOfWeek
        : [DayOfWeek];
}

public static partial class BackupSchedule
{
    public const string Default = "Täglich · 02:00";

    public static string Normalize(string? value) => TryParse(value, out var definition)
        ? Format(definition)
        : Default;

    public static bool TryParse(string? value, out BackupScheduleDefinition definition)
    {
        definition = new BackupScheduleDefinition(BackupScheduleKind.Daily, 0, new TimeOnly(2, 0), DayOfWeek.Sunday);
        if (string.IsNullOrWhiteSpace(value)) return true;

        var schedule = value.Trim();
        var interval = IntervalPattern().Match(schedule);
        if (interval.Success && int.TryParse(interval.Groups["value"].Value, out var amount))
        {
            var unit = interval.Groups["unit"].Value;
            var kind = unit.StartsWith("Minute", StringComparison.OrdinalIgnoreCase)
                ? BackupScheduleKind.IntervalMinutes
                : BackupScheduleKind.IntervalHours;
            if (!IsValidInterval(kind, amount)) return false;
            definition = definition with { Kind = kind, IntervalValue = amount };
            return true;
        }

        if (schedule.StartsWith("Daily", StringComparison.OrdinalIgnoreCase) ||
            schedule.Contains("glich", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadTime(schedule, new TimeOnly(2, 0), out var time)) return false;
            definition = definition with { Kind = BackupScheduleKind.Daily, Time = time };
            return true;
        }

        if (schedule.StartsWith("Weekly", StringComparison.OrdinalIgnoreCase) ||
            schedule.Contains("chentlich", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadTime(schedule, new TimeOnly(3, 0), out var time)) return false;
            var days = ReadDays(schedule);
            if (days.Count == 0) days = [DayOfWeek.Sunday];
            definition = definition with
            {
                Kind = BackupScheduleKind.Weekly,
                Time = time,
                DayOfWeek = days[0],
                DaysOfWeek = days
            };
            return true;
        }

        return false;
    }

    public static bool TryBuild(
        string? kind,
        int intervalValue,
        string? intervalUnit,
        string? timeValue,
        string? weekday,
        out string schedule,
        out string? error) => TryBuild(
            kind,
            intervalValue,
            intervalUnit,
            timeValue,
            string.IsNullOrWhiteSpace(weekday) ? [] : [weekday],
            out schedule,
            out error);

    public static bool TryBuild(
        string? kind,
        int intervalValue,
        string? intervalUnit,
        string? timeValue,
        IReadOnlyCollection<string>? weekdays,
        out string schedule,
        out string? error)
    {
        schedule = Default;
        error = null;
        if (string.Equals(kind, "Interval", StringComparison.OrdinalIgnoreCase))
        {
            var scheduleKind = string.Equals(intervalUnit, "Minutes", StringComparison.OrdinalIgnoreCase)
                ? BackupScheduleKind.IntervalMinutes
                : BackupScheduleKind.IntervalHours;
            if (!IsValidInterval(scheduleKind, intervalValue))
            {
                error = scheduleKind == BackupScheduleKind.IntervalMinutes
                    ? "Das Minutenintervall muss zwischen 1 und 1440 liegen."
                    : "Das Stundenintervall muss zwischen 1 und 168 liegen.";
                return false;
            }
            schedule = Format(new BackupScheduleDefinition(scheduleKind, intervalValue, default, DayOfWeek.Sunday));
            return true;
        }

        if (!TimeOnly.TryParseExact(timeValue, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            error = "Bitte eine gültige Uhrzeit angeben.";
            return false;
        }

        if (string.Equals(kind, "Weekly", StringComparison.OrdinalIgnoreCase))
        {
            var days = (weekdays ?? [])
                .Select(value => Enum.TryParse<DayOfWeek>(value, true, out var day) ? day : (DayOfWeek?)null)
                .Where(day => day is not null)
                .Select(day => day!.Value)
                .Distinct()
                .OrderBy(DaySortOrder)
                .ToList();
            if (days.Count == 0)
            {
                error = "Bitte mindestens einen Wochentag auswählen.";
                return false;
            }
            schedule = Format(new BackupScheduleDefinition(BackupScheduleKind.Weekly, 0, time, days[0], days));
            return true;
        }

        schedule = Format(new BackupScheduleDefinition(BackupScheduleKind.Daily, 0, time, DayOfWeek.Sunday));
        return true;
    }

    public static DateTimeOffset GetNextOccurrenceUtc(string? schedule, DateTimeOffset afterUtc, TimeZoneInfo? timeZone = null)
    {
        if (!TryParse(schedule, out var definition)) definition = ParseDefault();
        if (definition.Kind == BackupScheduleKind.IntervalMinutes)
            return afterUtc.AddMinutes(definition.IntervalValue);
        if (definition.Kind == BackupScheduleKind.IntervalHours)
            return afterUtc.AddHours(definition.IntervalValue);

        var zone = timeZone ?? ResolveTimeZone();
        var localAfter = TimeZoneInfo.ConvertTime(afterUtc, zone);
        if (definition.Kind == BackupScheduleKind.Weekly)
        {
            return definition.EffectiveDays
                .Distinct()
                .Select(day => GetNextWeeklyOccurrence(day, definition.Time, localAfter, zone))
                .Min();
        }

        var localCandidate = DateTime.SpecifyKind(localAfter.Date + definition.Time.ToTimeSpan(), DateTimeKind.Unspecified);
        if (localCandidate <= localAfter.DateTime)
            localCandidate = localCandidate.AddDays(1);
        if (zone.IsInvalidTime(localCandidate)) localCandidate = localCandidate.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localCandidate, zone), TimeSpan.Zero);
    }

    public static TimeZoneInfo ResolveTimeZone()
    {
        var configured = Environment.GetEnvironmentVariable("MATBU_TIME_ZONE") ?? "Europe/Berlin";
        try { return TimeZoneInfo.FindSystemTimeZoneById(configured); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    public static string Format(BackupScheduleDefinition definition) => definition.Kind switch
    {
        BackupScheduleKind.IntervalMinutes => $"Alle {definition.IntervalValue} {(definition.IntervalValue == 1 ? "Minute" : "Minuten")}",
        BackupScheduleKind.IntervalHours => $"Alle {definition.IntervalValue} {(definition.IntervalValue == 1 ? "Stunde" : "Stunden")}",
        BackupScheduleKind.Weekly => $"Wöchentlich · {string.Join(", ", definition.EffectiveDays.Distinct().OrderBy(DaySortOrder).Select(DayLabel))} {definition.Time:HH\\:mm}",
        _ => $"Täglich · {definition.Time:HH\\:mm}"
    };

    private static BackupScheduleDefinition ParseDefault()
    {
        TryParse(Default, out var definition);
        return definition;
    }

    private static bool IsValidInterval(BackupScheduleKind kind, int value) => kind switch
    {
        BackupScheduleKind.IntervalMinutes => value is >= 1 and <= 1440,
        BackupScheduleKind.IntervalHours => value is >= 1 and <= 168,
        _ => false
    };

    private static bool TryReadTime(string value, TimeOnly fallback, out TimeOnly time)
    {
        var match = TimePattern().Match(value);
        if (!match.Success) { time = fallback; return true; }
        return TimeOnly.TryParseExact(match.Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time) ||
               TimeOnly.TryParseExact(match.Value, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    private static List<DayOfWeek> ReadDays(string value)
    {
        return DayPattern().Matches(value)
            .Select(match => ParseDay(match.Value))
            .Distinct()
            .OrderBy(DaySortOrder)
            .ToList();
    }

    private static DayOfWeek ParseDay(string value) => value.ToLowerInvariant() switch
        {
            "mo" or "mon" => DayOfWeek.Monday,
            "di" or "tue" => DayOfWeek.Tuesday,
            "mi" or "wed" => DayOfWeek.Wednesday,
            "do" or "thu" => DayOfWeek.Thursday,
            "fr" or "fri" => DayOfWeek.Friday,
            "sa" or "sat" => DayOfWeek.Saturday,
            _ => DayOfWeek.Sunday
        };

    private static DateTimeOffset GetNextWeeklyOccurrence(DayOfWeek day, TimeOnly time, DateTimeOffset localAfter, TimeZoneInfo zone)
    {
        var daysAhead = ((int)day - (int)localAfter.DayOfWeek + 7) % 7;
        var candidateDate = localAfter.Date.AddDays(daysAhead);
        var localCandidate = DateTime.SpecifyKind(candidateDate + time.ToTimeSpan(), DateTimeKind.Unspecified);
        if (localCandidate <= localAfter.DateTime) localCandidate = localCandidate.AddDays(7);
        if (zone.IsInvalidTime(localCandidate)) localCandidate = localCandidate.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localCandidate, zone), TimeSpan.Zero);
    }

    private static int DaySortOrder(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    private static string DayLabel(DayOfWeek value) => value switch
    {
        DayOfWeek.Monday => "Mo",
        DayOfWeek.Tuesday => "Di",
        DayOfWeek.Wednesday => "Mi",
        DayOfWeek.Thursday => "Do",
        DayOfWeek.Friday => "Fr",
        DayOfWeek.Saturday => "Sa",
        _ => "So"
    };

    [GeneratedRegex(@"^Alle\s+(?<value>\d+)\s+(?<unit>Minuten?|Stunden?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntervalPattern();

    [GeneratedRegex(@"\b(?:[01]?\d|2[0-3]):[0-5]\d\b", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"\b(?:Mo|Di|Mi|Do|Fr|Sa|So|Mon|Tue|Wed|Thu|Fri|Sat|Sun)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayPattern();
}
