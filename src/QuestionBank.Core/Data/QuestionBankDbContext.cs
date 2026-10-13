using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Export;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Frameworks;
using QuestionBank.Core.Hosting;
using QuestionBank.Core.Inventory;
using QuestionBank.Core.Quality;
using QuestionBank.Core.Review;

namespace QuestionBank.Core.Data;

public sealed class QuestionBankDbContext(DbContextOptions<QuestionBankDbContext> options) : DbContext(options)
{
    public DbSet<SourceFile> SourceFiles => Set<SourceFile>();
    public DbSet<ScanRun> ScanRuns => Set<ScanRun>();
    public DbSet<ScanDirectory> ScanDirectories => Set<ScanDirectory>();
    public DbSet<ProcessingQueueItem> ProcessingQueue => Set<ProcessingQueueItem>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<ExportRecord> Exports => Set<ExportRecord>();
    public DbSet<QuestionBand> QuestionBands => Set<QuestionBand>();
    public DbSet<ColumnTemplate> ColumnTemplates => Set<ColumnTemplate>();
    public DbSet<CompetencyFramework> Frameworks => Set<CompetencyFramework>();
    public DbSet<CompetencyIndicator> Indicators => Set<CompetencyIndicator>();
    public DbSet<WorkerCommand> WorkerCommands => Set<WorkerCommand>();
    public DbSet<WorkerState> WorkerStates => Set<WorkerState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceFile>(e =>
        {
            e.Property(x => x.RelativePath).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Extension).HasMaxLength(32);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.LastErrorCode).HasMaxLength(100);
            e.HasIndex(x => x.RelativePath).IsUnique();
            e.HasIndex(x => new { x.Status, x.Id });
        });

        modelBuilder.Entity<ScanRun>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.FailureCode).HasMaxLength(100);
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<ScanDirectory>(e =>
        {
            e.Property(x => x.RelativePath).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.LastErrorCode).HasMaxLength(100);
            // يمنع إدراج المجلد مرتين في العملية نفسها، حتى لو أُعيدت معالجة المجلد الأب بعد انقطاع.
            e.HasIndex(x => new { x.RunId, x.RelativePath }).IsUnique();
            e.HasIndex(x => new { x.RunId, x.Status, x.NextAttemptUtc });
            e.HasOne<ScanRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProcessingQueueItem>(e =>
        {
            e.ToTable("ProcessingQueue");
            e.Property(x => x.RelativePath).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.RequestedBy).HasMaxLength(256).IsRequired();
            e.HasIndex(x => new { x.Status, x.Id });
        });

        modelBuilder.Entity<Question>(e =>
        {
            e.Property(x => x.StableKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.OriginalCode).HasMaxLength(200);
            e.Property(x => x.AnswerDetector).HasMaxLength(64);
            e.Property(x => x.Confidence).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ReviewStatus).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.ReaderName).HasMaxLength(32);
            e.Property(x => x.ReaderVersion).HasMaxLength(16);
            e.Property(x => x.ReviewedBy).HasMaxLength(256);
            e.Property(x => x.MatchStatus).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.MatchMethod).HasMaxLength(32);
            e.Property(x => x.MatchedBy).HasMaxLength(256);
            e.HasIndex(x => x.IndicatorId);
            e.HasOne<CompetencyIndicator>().WithMany().HasForeignKey(x => x.IndicatorId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.StemHash).HasMaxLength(32);
            e.Property(x => x.FullHash).HasMaxLength(32);
            e.HasIndex(x => x.StemHash);
            e.HasIndex(x => x.FullHash);
            e.HasIndex(x => x.StableKey).IsUnique();
            e.HasIndex(x => x.SourceFileId);
            e.HasIndex(x => x.UploadId);
            e.HasIndex(x => x.ContentHash);
            e.HasIndex(x => x.ReviewStatus);
            e.HasOne<SourceFile>().WithMany().HasForeignKey(x => x.SourceFileId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Upload>().WithMany().HasForeignKey(x => x.UploadId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Questions_OneSource",
                "(SourceFileId IS NULL) <> (UploadId IS NULL)"));
        });

        modelBuilder.Entity<Upload>(e =>
        {
            e.Property(x => x.OriginalName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ClientPath).HasMaxLength(1024);
            e.Property(x => x.Extension).HasMaxLength(32);
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.StoredName).HasMaxLength(128).IsRequired();
            e.Property(x => x.UploadedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.LastErrorCode).HasMaxLength(100);
            e.HasIndex(x => x.Sha256).IsUnique();
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.Property(x => x.User).HasMaxLength(256).IsRequired();
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
            e.Property(x => x.OldValue).HasMaxLength(200);
            e.Property(x => x.NewValue).HasMaxLength(200);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.AtUtc);
        });

        modelBuilder.Entity<QuestionBand>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(16).IsRequired();
            e.HasIndex(x => new { x.Band, x.Key });
            e.HasIndex(x => x.QuestionId);
            e.HasOne<Question>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkerCommand>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.RequestedBy).HasMaxLength(256).IsRequired();
            e.HasIndex(x => new { x.ProcessedAtUtc, x.Id });
        });

        modelBuilder.Entity<WorkerState>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(32);
            e.Property(x => x.ProblemCode).HasMaxLength(100);
            e.Property(x => x.ProblemMessage).HasMaxLength(1000);
            e.Property(x => x.Host).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<CompetencyFramework>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.Property(x => x.Version).HasMaxLength(100).IsRequired();
            e.Property(x => x.SourceName).HasMaxLength(255).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.ImportedBy).HasMaxLength(256).IsRequired();
            e.HasIndex(x => new { x.Name, x.Version }).IsUnique();
        });

        modelBuilder.Entity<CompetencyIndicator>(e =>
        {
            e.Property(x => x.MainDomain).HasMaxLength(500).IsRequired();
            e.Property(x => x.SubDomain).HasMaxLength(500);
            e.Property(x => x.Code).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(2000).IsRequired();
            e.Property(x => x.SourceRow).HasMaxLength(50).IsRequired();
            e.HasIndex(x => new { x.FrameworkId, x.Order });
            e.HasOne<CompetencyFramework>().WithMany().HasForeignKey(x => x.FrameworkId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ColumnTemplate>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.HeaderKey).HasMaxLength(2000).IsRequired();
            e.Property(x => x.StemHeader).HasMaxLength(200).IsRequired();
            e.Property(x => x.AnswerHeader).HasMaxLength(200);
            e.Property(x => x.CodeHeader).HasMaxLength(200);
            e.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.HeaderKey).IsUnique();
        });

        modelBuilder.Entity<ExportRecord>(e =>
        {
            e.Property(x => x.Format).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RequestedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(128).IsRequired();
        });
    }
}
