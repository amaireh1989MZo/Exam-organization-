using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Data;

namespace QuestionBank.Core.Inventory;

public enum QueueItemStatus
{
    /// <summary>بانتظار عامل الاستخراج (المرحلة 2).</summary>
    Queued,
    Processing,
    Completed,
    Cancelled
}

/// <summary>
/// طلب معالجة مجلد من مجلد الشبكة. الجرد يسجّل الملفات؛ هذا الطابور يحدد ترتيب استخراجها.
/// عامل الاستخراج في المرحلة 2 يستهلك الطلبات بالترتيب.
/// </summary>
public sealed class ProcessingQueueItem
{
    public long Id { get; set; }

    /// <summary>المسار النسبي للمجلد بفاصل '/'. النص الفارغ يعني الجذر كله.</summary>
    public required string RelativePath { get; set; }

    public QueueItemStatus Status { get; set; } = QueueItemStatus.Queued;
    public required string RequestedBy { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>عدد الملفات تحت المجلد لحظة الطلب.</summary>
    public int FileCount { get; set; }
}

public sealed record FolderNode(
    string Name, string RelativePath, int FileCount, int Pending, int Completed, int Failed, int NeedsReview, bool HasChildren);

public sealed record FolderListing(string RelativePath, int DirectFiles, IReadOnlyList<FolderNode> Folders);

public sealed record QueueEntry(
    long Id, string RelativePath, string Status, string RequestedBy, DateTime RequestedAtUtc, int FileCount);

public enum EnqueueOutcome
{
    Added,
    AlreadyQueued,
    InvalidPath,
    UnknownFolder
}

/// <summary>شجرة المجلدات من قاعدة البيانات فقط (لا تلمس نظام الملفات)، وطابور المعالجة.</summary>
public sealed class FolderService(QuestionBankDbContext db, TimeProvider time)
{
    /// <summary>يقبل "" للجذر، أو مسارًا نسبيًا بمقاطع غير فارغة وبلا "." أو "..".</summary>
    public static bool IsValidRelativeFolder(string path)
    {
        if (path.Length == 0)
            return true;
        if (path.Length > 2048 || path.Contains('\\') || path.Contains('\0') || Path.IsPathRooted(path))
            return false;
        return path.Split('/').All(s => s.Length > 0 && s != "." && s != "..");
    }

    /// <summary>المجلدات المباشرة تحت مسار، مع عدد الملفات (تنازليًا) حسب الحالة. الملفات المفقودة مستبعدة.</summary>
    public async Task<FolderListing?> ListAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (!IsValidRelativeFolder(relativePath))
            return null;

        var prefix = relativePath.Length == 0 ? "" : relativePath + "/";
        var rows = await db.SourceFiles.AsNoTracking()
            .Where(f => !f.IsMissing && f.RelativePath.StartsWith(prefix))
            .Select(f => new { f.RelativePath, f.Status })
            .ToListAsync(cancellationToken);

        // StartsWith في SQLite قد يُترجم إلى LIKE غير حساس لحالة الأحرف اللاتينية؛ نعيد التحقق بدقة.
        rows = rows.Where(r => r.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (relativePath.Length > 0 && rows.Count == 0)
            return null;

        var direct = 0;
        var folders = new Dictionary<string, (int Total, int Pending, int Completed, int Failed, int Review, bool Deeper)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var rest = row.RelativePath[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                direct++;
                continue;
            }
            var name = rest[..slash];
            folders.TryGetValue(name, out var c);
            c.Total++;
            switch (row.Status)
            {
                case FileStatus.Pending or FileStatus.Processing: c.Pending++; break;
                case FileStatus.Completed: c.Completed++; break;
                case FileStatus.Failed: c.Failed++; break;
                case FileStatus.NeedsReview: c.Review++; break;
            }
            c.Deeper |= rest.IndexOf('/', slash + 1) >= 0;
            folders[name] = c;
        }

        var nodes = folders
            .OrderBy(kv => kv.Key, StringComparer.CurrentCulture)
            .Select(kv => new FolderNode(kv.Key, prefix + kv.Key, kv.Value.Total, kv.Value.Pending,
                kv.Value.Completed, kv.Value.Failed, kv.Value.Review, kv.Value.Deeper))
            .ToList();
        return new FolderListing(relativePath, direct, nodes);
    }

    public async Task<IReadOnlyList<QueueEntry>> GetQueueAsync(CancellationToken cancellationToken) =>
        await db.ProcessingQueue.AsNoTracking()
            .Where(q => q.Status == QueueItemStatus.Queued || q.Status == QueueItemStatus.Processing)
            .OrderBy(q => q.Id)
            .Select(q => new QueueEntry(q.Id, q.RelativePath, q.Status.ToString(), q.RequestedBy, q.RequestedAtUtc, q.FileCount))
            .ToListAsync(cancellationToken);

    public async Task<(EnqueueOutcome Outcome, QueueEntry? Entry)> EnqueueAsync(
        string relativePath, string requestedBy, CancellationToken cancellationToken)
    {
        if (!IsValidRelativeFolder(relativePath))
            return (EnqueueOutcome.InvalidPath, null);

        var existing = await db.ProcessingQueue.AsNoTracking().FirstOrDefaultAsync(q =>
            q.RelativePath == relativePath &&
            (q.Status == QueueItemStatus.Queued || q.Status == QueueItemStatus.Processing), cancellationToken);
        if (existing is not null)
            return (EnqueueOutcome.AlreadyQueued, ToEntry(existing));

        var prefix = relativePath.Length == 0 ? "" : relativePath + "/";
        var paths = await db.SourceFiles.AsNoTracking()
            .Where(f => !f.IsMissing && f.RelativePath.StartsWith(prefix))
            .Select(f => f.RelativePath)
            .ToListAsync(cancellationToken);
        var count = paths.Count(p => p.StartsWith(prefix, StringComparison.Ordinal));
        if (count == 0)
            return (EnqueueOutcome.UnknownFolder, null);

        var item = new ProcessingQueueItem
        {
            RelativePath = relativePath,
            RequestedBy = requestedBy,
            RequestedAtUtc = time.GetUtcNow().UtcDateTime,
            FileCount = count
        };
        db.ProcessingQueue.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return (EnqueueOutcome.Added, ToEntry(item));
    }

    public async Task<bool> CancelAsync(long id, CancellationToken cancellationToken)
    {
        var item = await db.ProcessingQueue.FirstOrDefaultAsync(
            q => q.Id == id && q.Status == QueueItemStatus.Queued, cancellationToken);
        if (item is null)
            return false;
        item.Status = QueueItemStatus.Cancelled;
        item.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static QueueEntry ToEntry(ProcessingQueueItem q) =>
        new(q.Id, q.RelativePath, q.Status.ToString(), q.RequestedBy, q.RequestedAtUtc, q.FileCount);
}
