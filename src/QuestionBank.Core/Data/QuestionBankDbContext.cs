using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Inventory;

namespace QuestionBank.Core.Data;

public sealed class QuestionBankDbContext(DbContextOptions<QuestionBankDbContext> options) : DbContext(options)
{
    public DbSet<SourceFile> SourceFiles => Set<SourceFile>();
    public DbSet<ScanRun> ScanRuns => Set<ScanRun>();
    public DbSet<ScanDirectory> ScanDirectories => Set<ScanDirectory>();
    public DbSet<ProcessingQueueItem> ProcessingQueue => Set<ProcessingQueueItem>();

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
    }
}
