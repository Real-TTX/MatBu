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
}
