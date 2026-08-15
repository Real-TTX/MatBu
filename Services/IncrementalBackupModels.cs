using System.Text.Json;
using System.Text.Json.Serialization;
using MatBu.Models;

namespace MatBu.Services;

public sealed class IncrementalBackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public string SnapshotToken { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskToken { get; set; } = "";
    public string TransferId { get; set; } = "";
    public string RepositoryKey { get; set; } = "";
    public BackupMethod Method { get; set; } = BackupMethod.ReverseIncremental;
    public int ChunkSizeBytes { get; set; } = 8 * 1024 * 1024;
    public DateTimeOffset CreateDate { get; set; } = DateTimeOffset.UtcNow;
    public List<IncrementalFileManifest> Files { get; set; } = [];

    [JsonIgnore] public long TotalBytes => Files.Sum(file => file.Length);
    [JsonIgnore] public long StoredBytes => Files.SelectMany(file => file.Chunks).Where(chunk => chunk.Changed).Sum(chunk => (long)chunk.Length);
    [JsonIgnore] public long ReusedBytes => Math.Max(0, TotalBytes - StoredBytes);
}

public sealed class IncrementalFileManifest
{
    public string RelativePath { get; set; } = "";
    public long Length { get; set; }
    public DateTimeOffset LastWriteDate { get; set; }
    public string ContentHash { get; set; } = "";
    public List<IncrementalChunkManifest> Chunks { get; set; } = [];
}

public sealed class IncrementalChunkManifest
{
    public int Sequence { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
    public string Hash { get; set; } = "";
    public bool Changed { get; set; } = true;
}

public sealed record IncrementalSourcePreparation(
    IncrementalBackupManifest Manifest,
    string WorkingDirectory,
    string ChunkDirectory,
    string ManifestPath);

public sealed record IncrementalApplyResult(
    string Destination,
    long TotalBytes,
    long StoredBytes,
    long ReusedBytes,
    int FileCount,
    string RepositoryManifestPath);

public sealed record IncrementalSourceCommandPayload(
    long TaskId,
    string TaskToken,
    int ChunkSizeMiB,
    GatewaySourceRequest Source,
    long JobId,
    string RepositoryKey);

public sealed record IncrementalTargetCommandPayload(
    long TaskId,
    string TaskToken,
    GatewayTargetRequest Target,
    long JobId,
    string RepositoryKey);

public sealed record IncrementalSnapshotExportPayload(
    long TaskId,
    string TaskToken,
    string SnapshotToken,
    GatewayTargetRequest Target);

public sealed record IncrementalManifestUploadResult(
    bool Success,
    IReadOnlyList<string> MissingHashes,
    long TotalBytes,
    long StoredBytes,
    long ReusedBytes,
    string Message);

public static class IncrementalManifestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task WriteAsync(string path, IncrementalBackupManifest manifest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(output, manifest, Options, cancellationToken);
        File.Move(partial, path, overwrite: true);
    }

    public static async Task<IncrementalBackupManifest> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<IncrementalBackupManifest>(input, Options, cancellationToken)
            ?? throw new InvalidDataException($"Das Incremental-Manifest '{path}' ist leer oder ungültig.");
    }
}
