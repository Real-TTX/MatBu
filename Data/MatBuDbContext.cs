using MatBu.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MatBu.Data;

public sealed class MatBuDbContext(DbContextOptions<MatBuDbContext> options) : DbContext(options)
{
    public DbSet<MatBuInstance> Instances => Set<MatBuInstance>();
    public DbSet<BackupObject> Objects => Set<BackupObject>();
    public DbSet<BackupTask> Tasks => Set<BackupTask>();
    public DbSet<JobLabel> JobLabels => Set<JobLabel>();
    public DbSet<BackupTaskLabel> BackupTaskLabels => Set<BackupTaskLabel>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<SmbCredential> SmbCredentials => Set<SmbCredential>();
    public DbSet<TransferJob> TransferJobs => Set<TransferJob>();
    public DbSet<JobStep> JobSteps => Set<JobStep>();
    public DbSet<SecondaryCommand> SecondaryCommands => Set<SecondaryCommand>();
    public DbSet<BackupSnapshot> BackupSnapshots => Set<BackupSnapshot>();
    public DbSet<BackupFile> BackupFiles => Set<BackupFile>();
    public DbSet<BackupFileChunk> BackupFileChunks => Set<BackupFileChunk>();
    public DbSet<BackupChunk> BackupChunks => Set<BackupChunk>();
    public DbSet<TransferChunk> TransferChunks => Set<TransferChunk>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MatBuInstance>().ToTable("MatBuInstance");
        modelBuilder.Entity<BackupObject>().ToTable("BackupObject");
        modelBuilder.Entity<BackupTask>().ToTable("BackupTask");
        modelBuilder.Entity<JobLabel>().ToTable("JobLabel");
        modelBuilder.Entity<BackupTaskLabel>().ToTable("BackupTaskLabel");
        modelBuilder.Entity<AppUser>().ToTable("User");
        modelBuilder.Entity<UserSession>().ToTable("UserSession");
        modelBuilder.Entity<SmbCredential>().ToTable("SmbCredential");
        modelBuilder.Entity<TransferJob>().ToTable("TransferJob");
        modelBuilder.Entity<JobStep>().ToTable("JobStep");
        modelBuilder.Entity<SecondaryCommand>().ToTable("SecondaryCommand");
        modelBuilder.Entity<BackupSnapshot>().ToTable("BackupSnapshot");
        modelBuilder.Entity<BackupFile>().ToTable("BackupFile");
        modelBuilder.Entity<BackupFileChunk>().ToTable("BackupFileChunk");
        modelBuilder.Entity<BackupChunk>().ToTable("BackupChunk");
        modelBuilder.Entity<TransferChunk>().ToTable("TransferChunk");
        modelBuilder.Entity<NotificationDelivery>().ToTable("NotificationDelivery");
        modelBuilder.Entity<BackupObject>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<BackupObject>().Property(x => x.Direction).HasConversion<string>();
        modelBuilder.Entity<BackupObject>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<AppUser>().Property(x => x.Role).HasConversion<string>();
        modelBuilder.Entity<MatBuInstance>().Property(x => x.Role).HasConversion<string>();
        modelBuilder.Entity<MatBuInstance>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<SecondaryCommand>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<BackupTask>().Property(x => x.Method).HasConversion<string>();
        modelBuilder.Entity<BackupTask>().Property(x => x.Compression).HasConversion<string>();
        modelBuilder.Entity<BackupTask>().Property(x => x.ConsistencyMode).HasConversion<string>();
        var transferJobMethod = modelBuilder.Entity<TransferJob>().Property(x => x.Method).HasConversion<string>();
        transferJobMethod.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        modelBuilder.Entity<TransferJob>().Property(x => x.Compression).HasConversion<string>();
        modelBuilder.Entity<TransferJob>().Property(x => x.ConsistencyMode).HasConversion<string>();
        modelBuilder.Entity<BackupSnapshot>().Property(x => x.Method).HasConversion<string>();

        modelBuilder.Entity<JobStep>().HasIndex(x => new { x.TransferJobId, x.Sequence });
        modelBuilder.Entity<JobLabel>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<BackupTaskLabel>().HasIndex(x => new { x.BackupTaskId, x.JobLabelId }).IsUnique();
        modelBuilder.Entity<TransferJob>().HasIndex(x => x.SnapshotId);
        modelBuilder.Entity<BackupSnapshot>().HasIndex(x => x.Token).IsUnique();
        modelBuilder.Entity<BackupSnapshot>().HasIndex(x => x.TransferJobId).IsUnique();
        modelBuilder.Entity<BackupSnapshot>().HasIndex(x => new { x.TaskId, x.CreateDate });
        modelBuilder.Entity<BackupFile>().HasIndex(x => new { x.SnapshotId, x.RelativePath }).IsUnique();
        modelBuilder.Entity<BackupFileChunk>().HasIndex(x => new { x.BackupFileId, x.Sequence }).IsUnique();
        modelBuilder.Entity<BackupFileChunk>().HasIndex(x => x.Hash);
        modelBuilder.Entity<BackupChunk>().HasIndex(x => x.Hash).IsUnique();
        modelBuilder.Entity<TransferChunk>().HasIndex(x => new { x.TransferId, x.Sequence }).IsUnique();
        modelBuilder.Entity<TransferChunk>().HasIndex(x => new { x.TransferId, x.State });
        modelBuilder.Entity<TransferChunk>().HasIndex(x => x.Hash);
        modelBuilder.Entity<NotificationDelivery>().HasIndex(x => new { x.TransferJobId, x.Event, x.Channel }).IsUnique();
        modelBuilder.Entity<NotificationDelivery>().HasIndex(x => new { x.State, x.NextAttemptDate });

        foreach (var type in new[]
                 {
                     typeof(MatBuInstance), typeof(BackupObject), typeof(BackupTask), typeof(JobLabel), typeof(BackupTaskLabel), typeof(AppUser),
                     typeof(UserSession), typeof(JobStep), typeof(BackupSnapshot), typeof(BackupFile),
                     typeof(BackupFileChunk), typeof(BackupChunk), typeof(TransferChunk), typeof(NotificationDelivery)
                 })
            modelBuilder.Entity(type).Property<DateTimeOffset>("CreateDate").IsRequired();
    }
}
