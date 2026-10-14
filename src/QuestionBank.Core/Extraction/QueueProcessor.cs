using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuestionBank.Core.Data;
using QuestionBank.Core.Inventory;

namespace QuestionBank.Core.Extraction;

/// <summary>
/// يستهلك طابور المعالجة بالترتيب: لكل مجلد مطلوب، يقرأ ملفاته المنتظرة التي لها قارئ (قراءة فقط)،
/// ويستخرج أسئلتها. الملفات التي لا قارئ لصيغتها بعد تبقى Pending حتى يُضاف قارئها.
/// </summary>
public sealed class QueueProcessor(
    QuestionBankDbContext db,
    ISourceFileSystem fileSystem,
    IOptions<InventoryOptions> inventoryOptions,
    IOptions<UploadOptions> uploadOptions,
    ExtractionService extraction,
    ReaderRegistry readers,
    TimeProvider time,
    ILogger<QueueProcessor> logger)
{
    private const int BatchSize = 25;

    /// <summary>يعالج دفعة واحدة. يعيد false إذا لم يبقَ في الطابور شيء.</summary>
    public async Task<bool> ProcessNextBatchAsync(CancellationToken cancellationToken)
    {
        var item = await db.ProcessingQueue
            .Where(q => q.Status == QueueItemStatus.Queued || q.Status == QueueItemStatus.Processing)
            .OrderBy(q => q.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (item is null)
            return false;

        var opt = inventoryOptions.Value;
        if (string.IsNullOrWhiteSpace(opt.SourceRoot) || !fileSystem.DirectoryExists(opt.SourceRoot))
            throw new SourceUnavailableException();
        var root = SourcePaths.NormalizeRoot(opt.SourceRoot);

        if (item.Status == QueueItemStatus.Queued)
        {
            item.Status = QueueItemStatus.Processing;
            await db.SaveChangesAsync(cancellationToken);
        }

        var supported = readers.SupportedExtensions.ToList();
        var prefix = item.RelativePath.Length == 0 ? "" : item.RelativePath + "/";
        var candidates = await db.SourceFiles
            .Where(f => !f.IsMissing && f.Status == FileStatus.Pending && f.Extension != null
                        && supported.Contains(f.Extension) && f.RelativePath.StartsWith(prefix))
            .OrderBy(f => f.Id)
            .Take(BatchSize * 4)
            .ToListAsync(cancellationToken);
        var batch = candidates.Where(f => f.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).Take(BatchSize).ToList();

        if (batch.Count == 0)
        {
            item.Status = QueueItemStatus.Completed;
            item.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Queue item {QueueItemId} completed.", item.Id);
            return true;
        }

        foreach (var file in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessFileAsync(file, root, opt, cancellationToken);
        }
        return true;
    }

    private async Task ProcessFileAsync(SourceFile file, string root, InventoryOptions opt, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var full = SourcePaths.ToFullPath(root, file.RelativePath);
        if (full is null)
        {
            Fail(file, InventoryErrorCodes.OutsideRoot, now);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        if (file.SizeBytes > uploadOptions.Value.MaxBytes)
        {
            Fail(file, UploadErrorCodes.TooLarge, now);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        byte[] content;
        try
        {
            content = fileSystem.ReadFile(full, uploadOptions.Value.MaxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!fileSystem.DirectoryExists(root))
                throw new SourceUnavailableException();
            file.Attempts++;
            var code = InventoryErrors.Classify(ex);
            if (InventoryErrors.IsTransient(ex) && file.Attempts < opt.MaxRetries)
            {
                file.LastErrorCode = code;
                file.LastErrorAtUtc = now;
            }
            else
            {
                Fail(file, code, now);
            }
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Source file {FileId} could not be read ({ErrorCode}).", file.Id, code);
            return;
        }

        file.ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        await db.SaveChangesAsync(cancellationToken);

        var outcome = await extraction.ExtractAsync(content, file.Extension, file.Id, null, cancellationToken);
        file.Attempts++;
        if (outcome.NeedsMapping)
        {
            file.Status = FileStatus.NeedsReview;
            file.LastErrorCode = UploadService.NeedsColumnMapping;
            file.ProcessedAtUtc = now;
        }
        else if (outcome.ErrorCode is null)
        {
            file.Status = FileStatus.Completed;
            file.LastErrorCode = null;
            file.ProcessedAtUtc = now;
        }
        else
        {
            Fail(file, outcome.ErrorCode, now);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Fail(SourceFile file, string code, DateTime now)
    {
        file.Status = FileStatus.Failed;
        file.LastErrorCode = code;
        file.LastErrorAtUtc = now;
    }
}
