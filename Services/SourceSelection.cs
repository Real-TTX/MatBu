using System.Text.Json;

namespace MatBu.Services;

public static class SourceSelection
{
    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return Normalize(JsonSerializer.Deserialize<List<string>>(json) ?? []);
        }
        catch (JsonException) { return []; }
    }

    public static string Serialize(IEnumerable<string>? paths) => JsonSerializer.Serialize(Normalize(paths ?? []));

    public static IReadOnlyList<string> Normalize(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var value in paths)
        {
            var candidate = (value ?? "").Replace('\\', '/').Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains('\0'))) continue;
            var normalized = string.Join('/', segments);
            if (result.Any(existing => normalized.Equals(existing, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(existing + "/", StringComparison.OrdinalIgnoreCase))) continue;
            result.RemoveAll(existing => existing.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase));
            result.Add(normalized);
        }
        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Includes(string relativePath, IReadOnlyList<string> selectedPaths)
    {
        if (selectedPaths.Count == 0) return true;
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        return selectedPaths.Any(selected =>
            normalized.Equals(selected, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(selected + "/", StringComparison.OrdinalIgnoreCase));
    }
}
