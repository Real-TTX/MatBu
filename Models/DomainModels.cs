namespace MatBu.Models;

using System.ComponentModel.DataAnnotations.Schema;

public enum ObjectKind { Smb, LocalFolder, MatBuSlave, DockerVolume, Proxmox, ProxmoxBackupServer }
public enum ObjectDirection { Source, Target, Both }
public enum ObjectStatus { Healthy, Warning, Offline }
public enum UserRole { Admin, User, Operator }
public enum InstanceRole { Primary, Secondary }
public enum InstanceStatus { Unknown, Online, Offline }
public enum SecondaryCommandKind
{
    ObjectTest,
    ExportSource,
    ImportTarget,
    ExportArchive,
    ApplyRestore,
    PrepareIncrementalSource,
    ApplyIncrementalTarget,
    ExportIncrementalSnapshot,
    ApplyRetention,
    BrowseSource,
    CreateProxmoxNativeBackup
}
public enum BackupMethod { Full, ForwardIncremental, Differential, ReverseIncremental, ProxmoxNative }
public enum BackupCompression { None, Fast, Balanced, Maximum }
public enum BackupConsistencyMode { None, DockerPause, DockerExec }

public abstract class AuditedEntity
{
    public long Id { get; set; }
    public DateTimeOffset CreateDate { get; set; }
    public long CreateUserId { get; set; }
    public DateTimeOffset UpdateDate { get; set; }
    public long UpdateUserId { get; set; }
}

public sealed class BackupObject : AuditedEntity
{
    public string Name { get; set; } = "";
    public ObjectKind Kind { get; set; }
    public ObjectDirection Direction { get; set; }
    public string Location { get; set; } = "";
    public ObjectStatus Status { get; set; }
    public string Detail { get; set; } = "";
    public DateTimeOffset? LastTestDate { get; set; }
    public string LastTestMessage { get; set; } = "";
    public long InstanceId { get; set; } = 1;
    [NotMapped] public string? SmbUsername { get; set; }
    [NotMapped] public string? SmbPassword { get; set; }
}

public sealed class MatBuInstance : AuditedEntity
{
    public string Name { get; set; } = "";
    public InstanceRole Role { get; set; }
    public string Endpoint { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public InstanceStatus Status { get; set; } = InstanceStatus.Unknown;
    public DateTimeOffset? LastSeenDate { get; set; }
    public string LastMessage { get; set; } = "";
}

public sealed class SmbCredential
{
    public long Id { get; set; }
    public long ObjectId { get; set; }
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public DateTimeOffset UpdateDate { get; set; }
}

public sealed class TransferJob
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public string TaskToken { get; set; } = "";
    public string TaskName { get; set; } = "";
    public string LabelSnapshotJson { get; set; } = "[]";
    public string TransferId { get; set; } = "";
    public int Attempt { get; set; } = 1;
    public string State { get; set; } = "Running";
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public long SpeedBytesPerSecond { get; set; }
    public BackupMethod Method { get; set; } = BackupMethod.Full;
    public BackupCompression Compression { get; set; } = BackupCompression.None;
    public BackupConsistencyMode ConsistencyMode { get; set; }
    public string ConsistencyContainerNames { get; set; } = "";
    public int ConsistencyTimeoutSeconds { get; set; } = 60;
    public long EstimatedStoredBytes { get; set; }
    public string SourceSelectionJson { get; set; } = "[]";
    public long SnapshotId { get; set; }
    public long SourceBytes { get; set; }
    public long StoredBytes { get; set; }
    public long ReusedBytes { get; set; }
    public string CheckpointPath { get; set; } = "";
    public long SourceObjectId { get; set; }
    public string SourceObjectName { get; set; } = "";
    public string SourceObjectKind { get; set; } = "";
    public string SourceLocation { get; set; } = "";
    public long SourceInstanceId { get; set; }
    public string SourceInstanceName { get; set; } = "";
    public long TargetObjectId { get; set; }
    public string TargetObjectName { get; set; } = "";
    public string TargetObjectKind { get; set; } = "";
    public string TargetLocation { get; set; } = "";
    public long TargetInstanceId { get; set; }
    public string TargetInstanceName { get; set; } = "";
    public string ResolvedDestination { get; set; } = "";
    public bool RetentionExpired { get; set; }
    public string Error { get; set; } = "";
    public DateTimeOffset CreateDate { get; set; }
    public DateTimeOffset UpdateDate { get; set; }
}

public sealed class JobStep : AuditedEntity
{
    public long TransferJobId { get; set; }
    public int Sequence { get; set; }
    public string Stage { get; set; } = "";
    public string State { get; set; } = "Info";
    public string Message { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string Location { get; set; } = "";
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class SecondaryCommand : AuditedEntity
{
    public long InstanceId { get; set; }
    public SecondaryCommandKind Kind { get; set; }
    public string TransferId { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string State { get; set; } = "Queued";
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public long SpeedBytesPerSecond { get; set; }
    public string ResultJson { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class BackupTask : AuditedEntity
{
    public string Token { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public long SourceId { get; set; }
    public long TargetId { get; set; }
    public BackupMethod Method { get; set; } = BackupMethod.Full;
    public BackupCompression Compression { get; set; } = BackupCompression.Fast;
    public BackupConsistencyMode ConsistencyMode { get; set; }
    public string ConsistencyContainerNames { get; set; } = "";
    public string PreBackupCommand { get; set; } = "";
    public string PostBackupCommand { get; set; } = "";
    public int ConsistencyTimeoutSeconds { get; set; } = 60;
    public string SourceSelectionJson { get; set; } = "[]";
    public int ChunkSizeMiB { get; set; } = 8;
    public string Schedule { get; set; } = "Täglich · 02:00";
    public string Retention { get; set; } = "30 Tage";
    public int MaxRetryAttempts { get; set; } = 5;
    public int RetryDelayMinutes { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastRun { get; set; }
    public DateTimeOffset? NextRunDate { get; set; }
    public DateTimeOffset? NextRetryDate { get; set; }
    public string State { get; set; } = "Bereit";
    [NotMapped] public List<long> LabelIds { get; set; } = [];
}

public sealed class JobLabel : AuditedEntity
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#0b7f8a";
}

public sealed class BackupTaskLabel : AuditedEntity
{
    public long BackupTaskId { get; set; }
    public long JobLabelId { get; set; }
}

public sealed class BackupSnapshot : AuditedEntity
{
    public long TaskId { get; set; }
    public long TransferJobId { get; set; }
    public string Token { get; set; } = Guid.NewGuid().ToString("N");
    public BackupMethod Method { get; set; } = BackupMethod.Full;
    public string State { get; set; } = "Creating";
    public string RootPath { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
    public long StoredBytes { get; set; }
    public long ReusedBytes { get; set; }
}

public sealed class BackupFile : AuditedEntity
{
    public long SnapshotId { get; set; }
    public string RelativePath { get; set; } = "";
    public long Length { get; set; }
    public DateTimeOffset LastWriteDate { get; set; }
    public string ContentHash { get; set; } = "";
}

public sealed class BackupFileChunk : AuditedEntity
{
    public long BackupFileId { get; set; }
    public int Sequence { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public string Hash { get; set; } = "";
}

public sealed class BackupChunk : AuditedEntity
{
    public string Hash { get; set; } = "";
    public long Length { get; set; }
    public string RelativePath { get; set; } = "";
    public long RefCount { get; set; }
}

public sealed class TransferChunk : AuditedEntity
{
    public string TransferId { get; set; } = "";
    public int Sequence { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public string Hash { get; set; } = "";
    public string State { get; set; } = "Pending";
}

public sealed class NotificationDelivery : AuditedEntity
{
    public long TransferJobId { get; set; }
    public string Event { get; set; } = "";
    public string Channel { get; set; } = "";
    public string State { get; set; } = "Pending";
    public int Attempt { get; set; }
    public DateTimeOffset? NextAttemptDate { get; set; }
    public DateTimeOffset? SentDate { get; set; }
    public string Error { get; set; } = "";
}

public sealed class AppUser : AuditedEntity
{
    public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
}

public sealed class UserSession : AuditedEntity
{
    public string Token { get; set; } = "";
    public long UserId { get; set; }
    public DateTimeOffset ExpiresDate { get; set; }
}

public sealed class AppData
{
    public List<MatBuInstance> Instances { get; set; } = [];
    public List<BackupObject> Objects { get; set; } = [];
    public List<BackupTask> Tasks { get; set; } = [];
    public List<JobLabel> JobLabels { get; set; } = [];
    public List<BackupTaskLabel> BackupTaskLabels { get; set; } = [];
    public List<AppUser> Users { get; set; } = [];
    public List<UserSession> UserSessions { get; set; } = [];
    public List<SmbCredential> SmbCredentials { get; set; } = [];
    public List<TransferJob> TransferJobs { get; set; } = [];
    public List<JobStep> JobSteps { get; set; } = [];
    public List<SecondaryCommand> SecondaryCommands { get; set; } = [];
    public List<BackupSnapshot> BackupSnapshots { get; set; } = [];
    public List<BackupFile> BackupFiles { get; set; } = [];
    public List<BackupFileChunk> BackupFileChunks { get; set; } = [];
    public List<BackupChunk> BackupChunks { get; set; } = [];
    public List<TransferChunk> TransferChunks { get; set; } = [];
    public List<NotificationDelivery> NotificationDeliveries { get; set; } = [];
}
