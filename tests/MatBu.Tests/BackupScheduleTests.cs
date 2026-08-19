using MatBu.Services;

namespace MatBu.Tests;

public sealed class BackupScheduleTests
{
    [Theory]
    [InlineData("Alle 15 Minuten", BackupScheduleKind.IntervalMinutes, 15)]
    [InlineData("Alle 4 Stunden", BackupScheduleKind.IntervalHours, 4)]
    public void ParsesIntervals(string value, BackupScheduleKind kind, int interval)
    {
        Assert.True(BackupSchedule.TryParse(value, out var result));
        Assert.Equal(kind, result.Kind);
        Assert.Equal(interval, result.IntervalValue);
    }

    [Fact]
    public void RejectsOutOfRangeInterval()
    {
        Assert.False(BackupSchedule.TryParse("Alle 1441 Minuten", out _));
    }

    [Fact]
    public void CalculatesNextWeeklyOccurrence()
    {
        var utc = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        var next = BackupSchedule.GetNextOccurrenceUtc("Wöchentlich · Mo 03:00", utc, TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 8, 17, 3, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void BuildsSundayAtThreeWithoutAnInterval()
    {
        var valid = BackupSchedule.TryBuild(
            "Weekly",
            0,
            "Hours",
            "03:00",
            nameof(DayOfWeek.Sunday),
            out var schedule,
            out var error);

        Assert.True(valid, error);
        Assert.Equal("Wöchentlich · So 03:00", schedule);
        Assert.True(BackupSchedule.TryParse(schedule, out var definition));
        Assert.Equal(BackupScheduleKind.Weekly, definition.Kind);
        Assert.Equal(DayOfWeek.Sunday, definition.DayOfWeek);
        Assert.Equal(new TimeOnly(3, 0), definition.Time);
        Assert.Equal(0, definition.IntervalValue);
    }

    [Fact]
    public void BuildsAndParsesMultipleWeeklyDays()
    {
        var valid = BackupSchedule.TryBuild(
            "Weekly",
            0,
            "Hours",
            "03:00",
            [nameof(DayOfWeek.Tuesday), nameof(DayOfWeek.Sunday)],
            out var schedule,
            out var error);

        Assert.True(valid, error);
        Assert.Equal("Wöchentlich · Di, So 03:00", schedule);
        Assert.True(BackupSchedule.TryParse(schedule, out var definition));
        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Sunday], definition.EffectiveDays);
    }

    [Theory]
    [InlineData("2026-08-17T04:00:00+00:00", "2026-08-18T03:00:00+00:00")]
    [InlineData("2026-08-18T04:00:00+00:00", "2026-08-23T03:00:00+00:00")]
    [InlineData("2026-08-23T04:00:00+00:00", "2026-08-25T03:00:00+00:00")]
    public void CalculatesNextOccurrenceAcrossMultipleWeeklyDays(string after, string expected)
    {
        var next = BackupSchedule.GetNextOccurrenceUtc(
            "Wöchentlich · Di, So 03:00",
            DateTimeOffset.Parse(after),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse(expected), next);
    }
}
