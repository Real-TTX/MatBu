using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatBu.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace MatBu.Data;

public sealed class PersistentStore
{
    private readonly DbContextOptions<MatBuDbContext> _options;
    private readonly object _gate = new();
    private readonly string _legacyPath;
    private readonly string _monitoringTokenPath;
    private readonly string _writeLockPath;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDataProtector _credentialProtector;
    private readonly IDataProtector _instanceTokenProtector;
    private readonly IDataProtector _secondaryCommandProtector;

    public PersistentStore(IHostEnvironment environment)
    {
        var directory = Environment.GetEnvironmentVariable("MATBU_DATA_PATH") ?? Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        var keysDirectory = Path.Combine(directory, "keys");
        Directory.CreateDirectory(keysDirectory);
        _legacyPath = Path.Combine(directory, "matbu.json");
        _monitoringTokenPath = Path.Combine(directory, "monitoring.token");
        _writeLockPath = Path.Combine(directory, "matbu.write.lock");
        _dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(keysDirectory));
        _credentialProtector = _dataProtectionProvider.CreateProtector("MatBu.SmbCredential.v1");
        _instanceTokenProtector = _dataProtectionProvider.CreateProtector("MatBu.InstanceToken.v1");
        _secondaryCommandProtector = _dataProtectionProvider.CreateProtector("MatBu.SecondaryCommand.v1");
        var builder = new DbContextOptionsBuilder<MatBuDbContext>().UseSqlite($"Data Source={Path.Combine(directory, "matbu.db")}");
        _options = builder.Options;
        using var initializationLock = AcquireProcessLock();
        using var db = new MatBuDbContext(_options);
        db.Database.EnsureCreated();
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupObject ADD COLUMN InstanceId INTEGER NOT NULL DEFAULT 1"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupObject ADD COLUMN LastTestDate TEXT NULL"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupObject ADD COLUMN LastTestMessage TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN SpeedBytesPerSecond INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN TransferId TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN TaskToken TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN TaskName TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN Attempt INTEGER NOT NULL DEFAULT 1"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN SourceObjectId INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN SourceObjectName TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN SourceObjectKind TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN SourceLocation TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN SourceInstanceId INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN SourceInstanceName TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN TargetObjectId INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN TargetObjectName TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN TargetObjectKind TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN TargetLocation TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN TargetInstanceId INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN TargetInstanceName TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN ResolvedDestination TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN Token TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN Method TEXT NOT NULL DEFAULT 'Full'"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN ChunkSizeMiB INTEGER NOT NULL DEFAULT 8"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN Compression TEXT NOT NULL DEFAULT 'Fast'"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN SourceSelectionJson TEXT NOT NULL DEFAULT '[]'"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN NextRunDate TEXT NULL"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN MaxRetryAttempts INTEGER NOT NULL DEFAULT 5"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN RetryDelayMinutes INTEGER NOT NULL DEFAULT 2"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN NextRetryDate TEXT NULL"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN ConsistencyMode TEXT NOT NULL DEFAULT 'None'"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN ConsistencyContainerNames TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN PreBackupCommand TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN PostBackupCommand TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE BackupTask ADD COLUMN ConsistencyTimeoutSeconds INTEGER NOT NULL DEFAULT 60"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN LabelSnapshotJson TEXT NOT NULL DEFAULT '[]'"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN RetentionExpired INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN Compression TEXT NOT NULL DEFAULT 'None'"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN EstimatedStoredBytes INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN SourceSelectionJson TEXT NOT NULL DEFAULT '[]'"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN ConsistencyMode TEXT NOT NULL DEFAULT 'None'"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN ConsistencyContainerNames TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN ConsistencyTimeoutSeconds INTEGER NOT NULL DEFAULT 60"); } catch (SqliteException) { }
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS JobLabel (Id INTEGER NOT NULL CONSTRAINT PK_JobLabel PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Color TEXT NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_JobLabel_Name ON JobLabel (Name COLLATE NOCASE)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS BackupTaskLabel (Id INTEGER NOT NULL CONSTRAINT PK_BackupTaskLabel PRIMARY KEY AUTOINCREMENT, BackupTaskId INTEGER NOT NULL, JobLabelId INTEGER NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_BackupTaskLabel_BackupTaskId_JobLabelId ON BackupTaskLabel (BackupTaskId, JobLabelId)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS MatBuInstance (Id INTEGER NOT NULL CONSTRAINT PK_MatBuInstance PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Role TEXT NOT NULL, Endpoint TEXT NOT NULL, ProtectedToken TEXT NOT NULL, Enabled INTEGER NOT NULL, Status TEXT NOT NULL, LastSeenDate TEXT NULL, LastMessage TEXT NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        var staleTestDetails = db.Objects.AsEnumerable().Where(x => x.Detail.StartsWith("SMB-Verbindung fehlgeschlagen", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var item in staleTestDetails) { item.LastTestMessage = item.Detail; item.LastTestDate ??= item.UpdateDate; item.Detail = ""; }
        if (staleTestDetails.Count > 0) db.SaveChanges();
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS SmbCredential (Id INTEGER NOT NULL CONSTRAINT PK_SmbCredential PRIMARY KEY AUTOINCREMENT, ObjectId INTEGER NOT NULL, Username TEXT NOT NULL, ProtectedPassword TEXT NOT NULL, UpdateDate TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS NotificationDelivery (Id INTEGER NOT NULL CONSTRAINT PK_NotificationDelivery PRIMARY KEY AUTOINCREMENT, TransferJobId INTEGER NOT NULL, Event TEXT NOT NULL, Channel TEXT NOT NULL, State TEXT NOT NULL, Attempt INTEGER NOT NULL, NextAttemptDate TEXT NULL, SentDate TEXT NULL, Error TEXT NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_NotificationDelivery_TransferJobId_Event_Channel ON NotificationDelivery (TransferJobId, Event, Channel)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_NotificationDelivery_State_NextAttemptDate ON NotificationDelivery (State, NextAttemptDate)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS TransferJob (Id INTEGER NOT NULL CONSTRAINT PK_TransferJob PRIMARY KEY AUTOINCREMENT, TaskId INTEGER NOT NULL, State TEXT NOT NULL, BytesTransferred INTEGER NOT NULL, TotalBytes INTEGER NOT NULL, CheckpointPath TEXT NOT NULL, Error TEXT NOT NULL, CreateDate TEXT NOT NULL, UpdateDate TEXT NOT NULL)");
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN Method TEXT NOT NULL DEFAULT 'Full'"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN SnapshotId INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN SourceBytes INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN StoredBytes INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TransferJob ADD COLUMN ReusedBytes INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
        db.Database.ExecuteSqlRaw("UPDATE BackupTask SET Method = 'Full' WHERE Method IS NULL OR TRIM(Method) = ''");
        db.Database.ExecuteSqlRaw("UPDATE BackupTask SET Compression = 'Fast' WHERE Compression IS NULL OR TRIM(Compression) = ''");
        db.Database.ExecuteSqlRaw("UPDATE BackupTask SET Compression = 'None' WHERE Method = 'ReverseIncremental'");
        db.Database.ExecuteSqlRaw("UPDATE BackupTask SET ChunkSizeMiB = 8 WHERE ChunkSizeMiB <= 0");
        db.Database.ExecuteSqlRaw("UPDATE BackupTask SET MaxRetryAttempts = 5 WHERE MaxRetryAttempts <= 0");
        db.Database.ExecuteSqlRaw("UPDATE BackupTask SET RetryDelayMinutes = 2 WHERE RetryDelayMinutes <= 0");
        db.Database.ExecuteSqlRaw("UPDATE TransferJob SET Method = 'Full' WHERE Method IS NULL OR TRIM(Method) = ''");
        db.Database.ExecuteSqlRaw("UPDATE TransferJob SET Compression = 'None' WHERE Compression IS NULL OR TRIM(Compression) = ''");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS JobStep (Id INTEGER NOT NULL CONSTRAINT PK_JobStep PRIMARY KEY AUTOINCREMENT, TransferJobId INTEGER NOT NULL, Sequence INTEGER NOT NULL, Stage TEXT NOT NULL, State TEXT NOT NULL, Message TEXT NOT NULL, InstanceName TEXT NOT NULL, Location TEXT NOT NULL, BytesTransferred INTEGER NOT NULL, TotalBytes INTEGER NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_JobStep_TransferJobId_Sequence ON JobStep (TransferJobId, Sequence)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS SecondaryCommand (Id INTEGER NOT NULL CONSTRAINT PK_SecondaryCommand PRIMARY KEY AUTOINCREMENT, InstanceId INTEGER NOT NULL, Kind TEXT NOT NULL, TransferId TEXT NOT NULL, PayloadJson TEXT NOT NULL, State TEXT NOT NULL, BytesTransferred INTEGER NOT NULL, TotalBytes INTEGER NOT NULL, SpeedBytesPerSecond INTEGER NOT NULL, ResultJson TEXT NOT NULL, Error TEXT NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS BackupSnapshot (Id INTEGER NOT NULL CONSTRAINT PK_BackupSnapshot PRIMARY KEY AUTOINCREMENT, TaskId INTEGER NOT NULL, TransferJobId INTEGER NOT NULL, Token TEXT NOT NULL, Method TEXT NOT NULL, State TEXT NOT NULL, RootPath TEXT NOT NULL, ManifestPath TEXT NOT NULL, FileCount INTEGER NOT NULL, TotalBytes INTEGER NOT NULL, StoredBytes INTEGER NOT NULL, ReusedBytes INTEGER NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS BackupFile (Id INTEGER NOT NULL CONSTRAINT PK_BackupFile PRIMARY KEY AUTOINCREMENT, SnapshotId INTEGER NOT NULL, RelativePath TEXT NOT NULL, Length INTEGER NOT NULL, LastWriteDate TEXT NOT NULL, ContentHash TEXT NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS BackupFileChunk (Id INTEGER NOT NULL CONSTRAINT PK_BackupFileChunk PRIMARY KEY AUTOINCREMENT, BackupFileId INTEGER NOT NULL, Sequence INTEGER NOT NULL, Offset INTEGER NOT NULL, Length INTEGER NOT NULL, Hash TEXT NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS BackupChunk (Id INTEGER NOT NULL CONSTRAINT PK_BackupChunk PRIMARY KEY AUTOINCREMENT, Hash TEXT NOT NULL, Length INTEGER NOT NULL, RelativePath TEXT NOT NULL, RefCount INTEGER NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS TransferChunk (Id INTEGER NOT NULL CONSTRAINT PK_TransferChunk PRIMARY KEY AUTOINCREMENT, TransferId TEXT NOT NULL, Sequence INTEGER NOT NULL, Offset INTEGER NOT NULL, Length INTEGER NOT NULL, Hash TEXT NOT NULL, State TEXT NOT NULL, CreateDate TEXT NOT NULL, CreateUserId INTEGER NOT NULL, UpdateDate TEXT NOT NULL, UpdateUserId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_TransferJob_SnapshotId ON TransferJob (SnapshotId)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_BackupSnapshot_Token ON BackupSnapshot (Token)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_BackupSnapshot_TransferJobId ON BackupSnapshot (TransferJobId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_BackupSnapshot_TaskId_CreateDate ON BackupSnapshot (TaskId, CreateDate)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_BackupFile_SnapshotId_RelativePath ON BackupFile (SnapshotId, RelativePath)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_BackupFileChunk_BackupFileId_Sequence ON BackupFileChunk (BackupFileId, Sequence)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_BackupFileChunk_Hash ON BackupFileChunk (Hash)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_BackupChunk_Hash ON BackupChunk (Hash)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_TransferChunk_TransferId_Sequence ON TransferChunk (TransferId, Sequence)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_TransferChunk_TransferId_State ON TransferChunk (TransferId, State)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_TransferChunk_Hash ON TransferChunk (Hash)");
        if (!db.Users.Any()) ImportOrSeed(db);
        if (!db.Instances.Any())
        {
            var now = DateTimeOffset.UtcNow;
            db.Instances.Add(new MatBuInstance { Id = 1, Name = "Primary", Role = InstanceRole.Primary, Status = InstanceStatus.Online, CreateDate = now, UpdateDate = now });
            db.SaveChanges();
        }
        BackfillTaskTokens(db);
        BackfillLegacyJobRoutes(db);
        DisableLegacySeedTasks(db);
    }

    public AppData Read()
    {
        lock (_gate)
        {
            using var db = new MatBuDbContext(_options);
            return new AppData
            {
                Instances = db.Instances.AsNoTracking().ToList(),
                Objects = db.Objects.AsNoTracking().ToList(),
                Tasks = db.Tasks.AsNoTracking().ToList(),
                JobLabels = db.JobLabels.AsNoTracking().ToList(),
                BackupTaskLabels = db.BackupTaskLabels.AsNoTracking().ToList(),
                Users = db.Users.AsNoTracking().ToList(),
                UserSessions = db.UserSessions.AsNoTracking().ToList(),
                SmbCredentials = db.SmbCredentials.AsNoTracking().ToList(),
                TransferJobs = db.TransferJobs.AsNoTracking().ToList(),
                JobSteps = db.JobSteps.AsNoTracking().ToList(),
                SecondaryCommands = db.SecondaryCommands.AsNoTracking().ToList(),
                BackupSnapshots = db.BackupSnapshots.AsNoTracking().ToList(),
                BackupFiles = db.BackupFiles.AsNoTracking().ToList(),
                BackupFileChunks = db.BackupFileChunks.AsNoTracking().ToList(),
                BackupChunks = db.BackupChunks.AsNoTracking().ToList(),
                TransferChunks = db.TransferChunks.AsNoTracking().ToList(),
                NotificationDeliveries = db.NotificationDeliveries.AsNoTracking().ToList()
            };
        }
    }

    private static void BackfillLegacyJobRoutes(MatBuDbContext db)
    {
        var jobs = db.TransferJobs.Where(x => x.TaskName == "").ToList();
        if (jobs.Count == 0) return;

        var tasks = db.Tasks.AsNoTracking().ToDictionary(x => x.Id);
        var objects = db.Objects.AsNoTracking().ToDictionary(x => x.Id);
        var instances = db.Instances.AsNoTracking().ToDictionary(x => x.Id);
        foreach (var job in jobs)
        {
            if (!tasks.TryGetValue(job.TaskId, out var task)) continue;
            job.TaskName = task.Name;

            if (objects.TryGetValue(task.SourceId, out var source))
            {
                job.SourceObjectId = source.Id;
                job.SourceObjectName = source.Name;
                job.SourceObjectKind = source.Kind.ToString();
                job.SourceLocation = source.Location;
                job.SourceInstanceId = source.InstanceId;
                job.SourceInstanceName = instances.GetValueOrDefault(source.InstanceId)?.Name ?? $"Instance #{source.InstanceId}";
            }

            if (objects.TryGetValue(task.TargetId, out var target))
            {
                job.TargetObjectId = target.Id;
                job.TargetObjectName = target.Name;
                job.TargetObjectKind = target.Kind.ToString();
                job.TargetLocation = target.Location;
                job.TargetInstanceId = target.InstanceId;
                job.TargetInstanceName = instances.GetValueOrDefault(target.InstanceId)?.Name ?? $"Instance #{target.InstanceId}";
            }
        }

        db.SaveChanges();
    }

    private static void DisableLegacySeedTasks(MatBuDbContext db)
    {
        var objects = db.Objects.AsNoTracking().Where(item => item.Id >= 1 && item.Id <= 3).ToDictionary(item => item.Id);
        var isLegacyTopology =
            objects.TryGetValue(1, out var first) && first.Kind == ObjectKind.Smb && first.Location == "\\\\nas-kunde\\daten" &&
            objects.TryGetValue(2, out var second) && second.Kind == ObjectKind.MatBuSlave && second.Location == "master.matbu.local" &&
            objects.TryGetValue(3, out var third) && third.Kind == ObjectKind.LocalFolder && third.Location == "/data/archive";
        if (!isLegacyTopology) return;

        var legacyRoutes = new HashSet<(long Id, long SourceId, long TargetId)>
        {
            (1, 1, 2),
            (2, 2, 3),
            (3, 3, 2)
        };
        var tasks = db.Tasks.Where(item => item.Id >= 1 && item.Id <= 3).ToList();
        var changed = false;
        foreach (var task in tasks.Where(task => legacyRoutes.Contains((task.Id, task.SourceId, task.TargetId))))
        {
            if (!task.Enabled) continue;
            task.Enabled = false;
            task.State = "Deaktiviert";
            task.UpdateDate = DateTimeOffset.UtcNow;
            changed = true;
        }

        if (changed) db.SaveChanges();
    }

    private static void BackfillTaskTokens(MatBuDbContext db)
    {
        var tasks = db.Tasks.ToList();
        var changed = false;
        foreach (var task in tasks.Where(item => string.IsNullOrWhiteSpace(item.Token)))
        {
            var identity = $"{task.Id}|{task.CreateDate:O}|{task.Name}";
            task.Token = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];
            changed = true;
        }

        if (changed) db.SaveChanges();

        var taskById = tasks.ToDictionary(item => item.Id);
        var jobs = db.TransferJobs.ToList();
        foreach (var job in jobs)
        {
            if (!taskById.TryGetValue(job.TaskId, out var task)) continue;
            if (!string.IsNullOrWhiteSpace(job.TaskName) && !job.TaskName.Equals(task.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (job.CreateDate < task.CreateDate.AddSeconds(-1)) continue;
            if (job.TaskToken == task.Token) continue;
            job.TaskToken = task.Token;
            changed = true;
        }

        if (changed) db.SaveChanges();
    }

    public string GetMonitoringToken()
    {
        lock (_gate)
        {
            if (File.Exists(_monitoringTokenPath)) return File.ReadAllText(_monitoringTokenPath).Trim();
            return RegenerateMonitoringTokenLocked();
        }
    }

    public string RegenerateMonitoringToken()
    {
        lock (_gate) return RegenerateMonitoringTokenLocked();
    }

    public bool IsMonitoringTokenValid(string? token) => !string.IsNullOrWhiteSpace(token) && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(GetMonitoringToken()));

    public bool IsInstanceTokenValid(string? token)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MATBU_INSTANCE_ROLE"), "Secondary", StringComparison.OrdinalIgnoreCase)) return false;
        var expected = Environment.GetEnvironmentVariable("MATBU_INSTANCE_TOKEN");
        return !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(expected) && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(expected));
    }

    public bool IsRegisteredSecondaryTokenValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        foreach (var instance in Read().Instances.Where(x => x.Role == InstanceRole.Secondary && x.Enabled))
        {
            var registered = GetInstanceToken(instance.Id);
            if (!string.IsNullOrWhiteSpace(registered) && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(registered))) return true;
        }
        return false;
    }

    private string RegenerateMonitoringTokenLocked()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        File.WriteAllText(_monitoringTokenPath, token);
        return token;
    }

    public void Update(Action<AppData> change)
    {
        lock (_gate)
        {
            using var processLock = AcquireProcessLock();
            var data = Read();
            if (data.Instances.Count == 0) data.Instances.Add(new MatBuInstance { Id = 1, Name = "Primary", Role = InstanceRole.Primary, Status = InstanceStatus.Online, CreateDate = DateTimeOffset.UtcNow, UpdateDate = DateTimeOffset.UtcNow });
            change(data);
            using var db = new MatBuDbContext(_options);
            using var transaction = db.Database.BeginTransaction();
            db.BackupFileChunks.RemoveRange(db.BackupFileChunks);
            db.BackupFiles.RemoveRange(db.BackupFiles);
            db.TransferChunks.RemoveRange(db.TransferChunks);
            db.BackupSnapshots.RemoveRange(db.BackupSnapshots);
            db.BackupChunks.RemoveRange(db.BackupChunks);
            db.Instances.RemoveRange(db.Instances);
            db.Objects.RemoveRange(db.Objects);
            db.Tasks.RemoveRange(db.Tasks);
            db.BackupTaskLabels.RemoveRange(db.BackupTaskLabels);
            db.JobLabels.RemoveRange(db.JobLabels);
            db.Users.RemoveRange(db.Users);
            db.UserSessions.RemoveRange(db.UserSessions);
            db.SmbCredentials.RemoveRange(db.SmbCredentials);
            db.JobSteps.RemoveRange(db.JobSteps);
            db.TransferJobs.RemoveRange(db.TransferJobs);
            db.SecondaryCommands.RemoveRange(db.SecondaryCommands);
            db.NotificationDeliveries.RemoveRange(db.NotificationDeliveries);
            db.Instances.AddRange(data.Instances);
            db.Objects.AddRange(data.Objects);
            db.Tasks.AddRange(data.Tasks);
            db.JobLabels.AddRange(data.JobLabels);
            db.BackupTaskLabels.AddRange(data.BackupTaskLabels);
            db.Users.AddRange(data.Users);
            db.UserSessions.AddRange(data.UserSessions);
            db.SmbCredentials.AddRange(data.SmbCredentials.Select(x => new SmbCredential { Id = x.Id, ObjectId = x.ObjectId, Username = x.Username, ProtectedPassword = x.ProtectedPassword, UpdateDate = x.UpdateDate }));
            db.TransferJobs.AddRange(data.TransferJobs);
            db.JobSteps.AddRange(data.JobSteps);
            db.SecondaryCommands.AddRange(data.SecondaryCommands);
            db.BackupSnapshots.AddRange(data.BackupSnapshots);
            db.BackupFiles.AddRange(data.BackupFiles);
            db.BackupFileChunks.AddRange(data.BackupFileChunks);
            db.BackupChunks.AddRange(data.BackupChunks);
            db.TransferChunks.AddRange(data.TransferChunks);
            db.NotificationDeliveries.AddRange(data.NotificationDeliveries);
            db.SaveChanges();
            transaction.Commit();
        }
    }

    private FileStream AcquireProcessLock()
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            try
            {
                return new FileStream(_writeLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 399)
            {
                Thread.Sleep(25);
            }
        }

        throw new IOException("Die MatBu-Datensperre konnte nicht erworben werden.");
    }

    public long NextId(IEnumerable<long> ids) => ids.DefaultIfEmpty().Max() + 1;

    public bool IsSessionValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var session = Read().UserSessions.FirstOrDefault(x => x.Token == token);
        return session is not null && session.ExpiresDate > DateTimeOffset.UtcNow;
    }

    public void SetSmbCredential(AppData data, long objectId, string username, string password)
    {
        var existing = data.SmbCredentials.FirstOrDefault(x => x.ObjectId == objectId);
        if (existing is null) data.SmbCredentials.Add(new SmbCredential { Id = NextId(data.SmbCredentials.Select(x => x.Id)), ObjectId = objectId, Username = username, ProtectedPassword = _credentialProtector.Protect(password), UpdateDate = DateTimeOffset.UtcNow });
        else { existing.Username = username; existing.ProtectedPassword = _credentialProtector.Protect(password); existing.UpdateDate = DateTimeOffset.UtcNow; }
    }

    public string ProtectSecondaryCommandPayload(string json) => "protected:v1:" + _secondaryCommandProtector.Protect(json);

    public string UnprotectSecondaryCommandPayload(string value)
    {
        const string prefix = "protected:v1:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return value;
        return _secondaryCommandProtector.Unprotect(value[prefix.Length..]);
    }

    public void SetInstanceToken(AppData data, long instanceId, string token)
    {
        var instance = data.Instances.FirstOrDefault(x => x.Id == instanceId);
        if (instance is not null) instance.ProtectedToken = string.IsNullOrWhiteSpace(token) ? "" : _instanceTokenProtector.Protect(token);
    }

    public string? GetInstanceToken(long instanceId)
    {
        var instance = Read().Instances.FirstOrDefault(x => x.Id == instanceId);
        if (instance is null || string.IsNullOrWhiteSpace(instance.ProtectedToken)) return null;
        try { return _instanceTokenProtector.Unprotect(instance.ProtectedToken); }
        catch (CryptographicException) { return null; }
    }

    public (string Username, string Password)? GetSmbCredential(long objectId)
    {
        var credential = Read().SmbCredentials.FirstOrDefault(x => x.ObjectId == objectId);
        if (credential is null) return null;
        try { return (credential.Username, _credentialProtector.Unprotect(credential.ProtectedPassword)); }
        catch (CryptographicException) { return null; }
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256$120000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private void ImportOrSeed(MatBuDbContext db)
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var data = File.Exists(_legacyPath)
            ? JsonSerializer.Deserialize<AppData>(File.ReadAllText(_legacyPath), jsonOptions)
            : null;
        data ??= Seed();
        if (data.Users.Count == 0) data.Users.Add(Seed().Users[0]);
        if (data.Users[0].PasswordHash == "local") data.Users[0].PasswordHash = HashPassword("admin");
        if (data.Instances.Count == 0) data.Instances.Add(new MatBuInstance { Id = 1, Name = "Primary", Role = InstanceRole.Primary, Status = InstanceStatus.Online, CreateDate = DateTimeOffset.UtcNow, UpdateDate = DateTimeOffset.UtcNow });
        db.Instances.AddRange(data.Instances);
        db.Objects.AddRange(data.Objects);
        db.Tasks.AddRange(data.Tasks);
        db.JobLabels.AddRange(data.JobLabels);
        db.BackupTaskLabels.AddRange(data.BackupTaskLabels);
        db.Users.AddRange(data.Users);
        db.UserSessions.AddRange(data.UserSessions);
        db.SmbCredentials.AddRange(data.SmbCredentials);
        db.TransferJobs.AddRange(data.TransferJobs);
        db.JobSteps.AddRange(data.JobSteps);
        db.SecondaryCommands.AddRange(data.SecondaryCommands);
        db.BackupSnapshots.AddRange(data.BackupSnapshots);
        db.BackupFiles.AddRange(data.BackupFiles);
        db.BackupFileChunks.AddRange(data.BackupFileChunks);
        db.BackupChunks.AddRange(data.BackupChunks);
        db.TransferChunks.AddRange(data.TransferChunks);
        db.SaveChanges();
    }

    private static AppData Seed()
    {
        var now = DateTimeOffset.UtcNow;
        return new AppData
        {
            Instances = [new MatBuInstance { Id = 1, Name = "Primary", Role = InstanceRole.Primary, Status = InstanceStatus.Online, CreateDate = now, UpdateDate = now }],
            Users = [new AppUser { Id = 1, UserName = "admin", PasswordHash = HashPassword("admin"), Role = UserRole.Admin, CreateDate = now, UpdateDate = now }]
        };
    }
}
