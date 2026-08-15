namespace MatBu.Services;

public sealed record SmbLocation(string Server, string ShareName, string? Directory)
{
    public string Share => $"//{Server}/{ShareName}";
    public string UncPath => string.IsNullOrWhiteSpace(Directory)
        ? $"\\\\{Server}\\{ShareName}"
        : $"\\\\{Server}\\{ShareName}\\{Directory.Replace('/', '\\')}";

    public string Summary => string.IsNullOrWhiteSpace(Directory)
        ? $"Server: {Server} · Freigabe: {ShareName}"
        : $"Server: {Server} · Freigabe: {ShareName} · Unterordner: {Directory.Replace('/', '\\')}";
}

public static class SmbPath
{
    public static SmbLocation Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException("Der SMB-Pfad darf nicht leer sein.");

        var normalized = value.Trim().Replace('\\', '/');
        if (normalized.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            normalized = "//" + normalized[6..];

        var parts = normalized.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new FormatException("Der SMB-Pfad muss Server und Freigabe enthalten, z. B. \\\\server\\freigabe\\ordner.");

        if (parts.Any(part => part is "." or ".." || part.Contains('\n') || part.Contains('\r')))
            throw new FormatException("Der SMB-Pfad enthält ein ungültiges Pfadsegment.");

        var directory = parts.Length > 2 ? string.Join('/', parts.Skip(2)) : null;
        return new SmbLocation(parts[0], parts[1], directory);
    }

    public static bool TryParse(string? value, out SmbLocation? location, out string? error)
    {
        try
        {
            location = Parse(value ?? string.Empty);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            location = null;
            error = ex.Message;
            return false;
        }
    }
}
