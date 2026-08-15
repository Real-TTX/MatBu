using System.Text.Json;
using MatBu.Models;

namespace MatBu.Services;

public sealed record JobLabelSnapshot(long Id, string Name, string Color);

public static class JobLabelSnapshots
{
    public static string Create(AppData data, long taskId)
    {
        var labelIds = data.BackupTaskLabels.Where(item => item.BackupTaskId == taskId).Select(item => item.JobLabelId).ToHashSet();
        var labels = data.JobLabels
            .Where(label => labelIds.Contains(label.Id))
            .OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase)
            .Select(label => new JobLabelSnapshot(label.Id, label.Name, NormalizeColor(label.Color)))
            .ToList();
        return JsonSerializer.Serialize(labels);
    }

    public static IReadOnlyList<JobLabelSnapshot> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<JobLabelSnapshot>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string NormalizeColor(string? color) =>
        !string.IsNullOrWhiteSpace(color) && color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit)
            ? color.ToLowerInvariant()
            : "#0b7f8a";
}
