using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Data;

namespace QuestionBank.Core.Inventory;

public sealed record RunSummary(
    long Id, string Status, DateTime StartedAtUtc, DateTime? FinishedAtUtc,
    long DirectoriesCompleted, long DirectoriesFailed, long DirectoriesPending,
    long FilesSeen, long FilesNew, long FilesChanged, long FilesMissing, long LinksSkipped, long EntryErrors);

public sealed record InventoryStatus(
    RunSummary? ActiveRun,
    RunSummary? LastFinishedRun,
    IReadOnlyDictionary<string, int> FilesByStatus,
    IReadOnlyDictionary<string, int> FilesByExtension,
    int FilesMissing,
    int FilesTotal);

/// <summary>استعلامات للوحة الحالة. لا تعيد مسارات، فقط أعدادًا ورموزًا.</summary>
public sealed class InventoryQueries(QuestionBankDbContext db)
{
    public async Task<InventoryStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var active = await db.ScanRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Status == ScanRunStatus.Running, cancellationToken);
        var last = await db.ScanRuns.AsNoTracking()
            .Where(r => r.Status != ScanRunStatus.Running)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var present = db.SourceFiles.AsNoTracking().Where(f => !f.IsMissing);
        var byStatus = await present.GroupBy(f => f.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key.ToString(), x => x.Count, cancellationToken);
        var byExt = await present.GroupBy(f => f.Extension ?? "")
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(20)
            .ToDictionaryAsync(x => x.Key.Length == 0 ? "(بدون امتداد)" : x.Key, x => x.Count, cancellationToken);
        var missing = await db.SourceFiles.CountAsync(f => f.IsMissing, cancellationToken);

        return new InventoryStatus(
            active is null ? null : await SummarizeAsync(active, cancellationToken),
            last is null ? null : await SummarizeAsync(last, cancellationToken),
            byStatus, byExt, missing, byStatus.Values.Sum());
    }

    private async Task<RunSummary> SummarizeAsync(ScanRun r, CancellationToken cancellationToken)
    {
        var pending = await db.ScanDirectories.CountAsync(
            d => d.RunId == r.Id && d.Status == ScanDirectoryStatus.Pending, cancellationToken);
        return new RunSummary(r.Id, r.Status.ToString(), r.StartedAtUtc, r.FinishedAtUtc,
            r.DirectoriesCompleted, r.DirectoriesFailed, pending,
            r.FilesSeen, r.FilesNew, r.FilesChanged, r.FilesMissing, r.LinksSkipped, r.EntryErrors);
    }
}
