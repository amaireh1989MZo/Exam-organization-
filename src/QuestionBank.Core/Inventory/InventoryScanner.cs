using System.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuestionBank.Core.Data;

namespace QuestionBank.Core.Inventory;

public sealed class InventoryConfigurationException(IReadOnlyList<string> errors)
    : Exception("إعدادات الجرد غير صالحة: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>جذر المصدر نفسه غير متاح (انقطاع الشبكة مثلًا). العملية تبقى قيد التشغيل وتُستأنف لاحقًا.</summary>
public sealed class SourceUnavailableException() : Exception("جذر المصدر غير متاح حاليًا.");

/// <summary>
/// جرد آمن قابل للاستئناف:
/// - طابور المجلدات محفوظ في قاعدة البيانات، وكل مجلد يُحفظ في معاملة واحدة مع عدادات العملية،
///   فالتوقف في أي لحظة لا يفقد التقدم ولا يكرر النتائج.
/// - لا يتبع الروابط الرمزية ولا junctions، ويرفض أي مسار يخرج عن الجذر.
/// - قراءة فقط: لا يفتح محتوى الملفات ولا يعدّل المصدر.
/// - لا يسجل المسارات أو المحتوى في السجلات؛ يسجل المعرّفات ورموز الأخطاء فقط.
/// </summary>
public sealed class InventoryScanner(
    QuestionBankDbContext db,
    ISourceFileSystem fileSystem,
    IOptions<InventoryOptions> options,
    TimeProvider time,
    ILogger<InventoryScanner> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// يستأنف العملية غير المكتملة إن وجدت، وإلا يبدأ عملية جديدة، ويستمر حتى ينتهي الطابور.
    /// الإلغاء عبر <paramref name="cancellationToken"/> يترك العملية قيد التشغيل لتُستأنف لاحقًا.
    /// </summary>
    public async Task<ScanRun> RunAsync(CancellationToken cancellationToken)
    {
        var opt = options.Value;
        var errors = SourcePaths.Validate(opt, fileSystem.DirectoryExists);
        if (errors.Count > 0)
            throw new InventoryConfigurationException(errors);

        var root = SourcePaths.NormalizeRoot(opt.SourceRoot);
        var run = await StartOrResumeAsync(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var next = await db.ScanDirectories
                .Where(d => d.RunId == run.Id && d.Status == ScanDirectoryStatus.Pending)
                .OrderBy(d => d.NextAttemptUtc).ThenBy(d => d.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (next is null)
                break;

            var wait = next.NextAttemptUtc - Now;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, cancellationToken);
                continue;
            }

            await ProcessDirectoryAsync(run, next, root, opt, cancellationToken);
        }

        await FinishAsync(run, cancellationToken);
        return run;
    }

    /// <summary>يتخلى عن العملية الجارية (بطلب المسؤول) فلا تُستأنف، والعملية التالية تبدأ من جديد.</summary>
    public async Task<bool> AbandonActiveRunAsync(CancellationToken cancellationToken)
    {
        var run = await db.ScanRuns.FirstOrDefaultAsync(r => r.Status == ScanRunStatus.Running, cancellationToken);
        if (run is null)
            return false;
        run.Status = ScanRunStatus.Cancelled;
        run.FinishedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Inventory run {RunId} abandoned.", run.Id);
        return true;
    }

    private async Task<ScanRun> StartOrResumeAsync(CancellationToken cancellationToken)
    {
        var run = await db.ScanRuns
            .Where(r => r.Status == ScanRunStatus.Running)
            .OrderBy(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is not null)
        {
            logger.LogInformation("Resuming inventory run {RunId}.", run.Id);
            return run;
        }

        run = new ScanRun { StartedAtUtc = Now };
        db.ScanRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        db.ScanDirectories.Add(new ScanDirectory { RunId = run.Id, RelativePath = "", NextAttemptUtc = Now });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Started inventory run {RunId}.", run.Id);
        return run;
    }

    private async Task ProcessDirectoryAsync(
        ScanRun run, ScanDirectory dir, string root, InventoryOptions opt, CancellationToken cancellationToken)
    {
        var fullPath = SourcePaths.ToFullPath(root, dir.RelativePath);
        if (fullPath is null)
        {
            FailDirectory(run, dir, InventoryErrorCodes.OutsideRoot);
            await SaveAsync(cancellationToken);
            return;
        }

        IReadOnlyList<SourceEntry> entries;
        try
        {
            entries = fileSystem.ListDirectory(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // إذا اختفى الجذر نفسه فالمشكلة في الشبكة وليست في هذا المجلد: لا نستهلك محاولاته.
            if (!fileSystem.DirectoryExists(root))
                throw new SourceUnavailableException();

            var code = InventoryErrors.Classify(ex);
            dir.Attempts++;
            dir.LastErrorCode = code;
            if (InventoryErrors.IsTransient(ex) && dir.Attempts < opt.MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(opt.RetryBaseDelaySeconds * Math.Pow(2, dir.Attempts - 1));
                dir.NextAttemptUtc = Now + delay;
                logger.LogWarning("Directory {DirectoryId} failed with {ErrorCode}; retry {Attempt} of {MaxRetries}.",
                    dir.Id, code, dir.Attempts, opt.MaxRetries);
            }
            else
            {
                FailDirectory(run, dir, code);
            }
            await SaveAsync(cancellationToken);
            return;
        }

        var now = Now;
        var fileEntries = entries.Where(e => !e.IsDirectory && !e.IsLink).ToList();
        var dirEntries = entries.Where(e => e.IsDirectory && !e.IsLink).ToList();
        run.LinksSkipped += entries.Count(e => e.IsLink);

        var filePaths = fileEntries.Select(e => SourcePaths.Combine(dir.RelativePath, e.Name)).ToList();
        var existingFiles = filePaths.Count == 0
            ? new Dictionary<string, SourceFile>()
            : await db.SourceFiles.Where(f => filePaths.Contains(f.RelativePath))
                .ToDictionaryAsync(f => f.RelativePath, StringComparer.Ordinal, cancellationToken);

        var dirPaths = dirEntries.Select(e => SourcePaths.Combine(dir.RelativePath, e.Name)).ToList();
        var queuedDirs = dirPaths.Count == 0
            ? new HashSet<string>()
            : (await db.ScanDirectories.Where(d => d.RunId == run.Id && dirPaths.Contains(d.RelativePath))
                .Select(d => d.RelativePath).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        foreach (var path in dirPaths.Where(p => !queuedDirs.Contains(p)))
            db.ScanDirectories.Add(new ScanDirectory { RunId = run.Id, RelativePath = path, NextAttemptUtc = now });

        for (var i = 0; i < fileEntries.Count; i++)
            UpsertFile(run, fileEntries[i], filePaths[i], existingFiles, now);

        dir.Status = ScanDirectoryStatus.Completed;
        dir.LastErrorCode = null;
        run.DirectoriesCompleted++;
        await SaveAsync(cancellationToken);
    }

    private void UpsertFile(
        ScanRun run, SourceEntry entry, string relativePath, Dictionary<string, SourceFile> existing, DateTime now)
    {
        run.FilesSeen++;
        if (entry.ErrorCode is not null)
            run.EntryErrors++;

        if (!existing.TryGetValue(relativePath, out var file))
        {
            db.SourceFiles.Add(new SourceFile
            {
                RelativePath = relativePath,
                Extension = ExtensionOf(entry.Name),
                SizeBytes = entry.Length,
                LastWriteUtc = entry.LastWriteUtc,
                Status = entry.ErrorCode is null ? FileStatus.Pending : FileStatus.Failed,
                LastErrorCode = entry.ErrorCode,
                LastErrorAtUtc = entry.ErrorCode is null ? null : now,
                DiscoveredAtUtc = now,
                LastSeenUtc = now,
                LastSeenRunId = run.Id
            });
            run.FilesNew++;
            return;
        }

        if (entry.ErrorCode is not null)
        {
            // لا نغيّر الحجم أو التاريخ المعروفين بقيم فارغة؛ نسجل الخطأ فقط.
            file.LastErrorCode = entry.ErrorCode;
            file.LastErrorAtUtc = now;
        }
        else if (file.SizeBytes != entry.Length || file.LastWriteUtc.Ticks != entry.LastWriteUtc.Ticks)
        {
            file.SizeBytes = entry.Length;
            file.LastWriteUtc = entry.LastWriteUtc;
            file.ContentHash = null;
            file.Status = FileStatus.Pending;
            file.Attempts = 0;
            file.LastErrorCode = null;
            file.LastErrorAtUtc = null;
            file.ChangeCount++;
            run.FilesChanged++;
        }

        file.IsMissing = false;
        file.LastSeenUtc = now;
        file.LastSeenRunId = run.Id;
    }

    private async Task FinishAsync(ScanRun run, CancellationToken cancellationToken)
    {
        // ملفات لم تُرَ في هذه العملية تُعلَّم مفقودة ولا تُحذف، إلا إذا كانت تحت مجلد تعذّر جرده:
        // عدم رؤيتها هناك لا يعني أنها اختفت.
        var failedDirs = await db.ScanDirectories
            .Where(d => d.RunId == run.Id && d.Status == ScanDirectoryStatus.Failed)
            .Select(d => d.RelativePath)
            .ToListAsync(cancellationToken);

        var unseen = await db.SourceFiles
            .Where(f => f.LastSeenRunId != run.Id && !f.IsMissing)
            .Select(f => new { f.Id, f.RelativePath })
            .ToListAsync(cancellationToken);

        var missingIds = unseen
            .Where(f => !failedDirs.Any(d => d.Length == 0 || f.RelativePath.StartsWith(d + "/", StringComparison.Ordinal)))
            .Select(f => f.Id)
            .ToList();

        foreach (var chunk in missingIds.Chunk(500))
        {
            await db.SourceFiles.Where(f => chunk.Contains(f.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsMissing, true), cancellationToken);
        }

        run.FilesMissing = missingIds.Count;
        run.Status = ScanRunStatus.Completed;
        run.FinishedAtUtc = Now;
        await SaveAsync(cancellationToken);

        // المجلدات المكتملة لا حاجة لها بعد انتهاء العملية؛ المتعذرة تبقى للتقرير.
        await db.ScanDirectories
            .Where(d => d.RunId == run.Id && d.Status == ScanDirectoryStatus.Completed)
            .ExecuteDeleteAsync(cancellationToken);

        logger.LogInformation(
            "Inventory run {RunId} completed: {Seen} seen, {New} new, {Changed} changed, {Missing} missing, {FailedDirs} directories failed, {Links} links skipped.",
            run.Id, run.FilesSeen, run.FilesNew, run.FilesChanged, run.FilesMissing, run.DirectoriesFailed, run.LinksSkipped);
    }

    private void FailDirectory(ScanRun run, ScanDirectory dir, string code)
    {
        dir.Status = ScanDirectoryStatus.Failed;
        dir.LastErrorCode = code;
        run.DirectoriesFailed++;
        logger.LogWarning("Directory {DirectoryId} failed permanently with {ErrorCode}.", dir.Id, code);
    }

    /// <summary>يحفظ ثم يفصل كل الكيانات عدا العملية، حتى لا تكبر الذاكرة مع 78 ألف ملف.</summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        // CancellationToken.None: إذا بدأ الحفظ فليكتمل، حتى تبقى عدادات العملية متسقة مع ما حُفظ.
        await db.SaveChangesAsync(CancellationToken.None);
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is not ScanRun).ToList())
            entry.State = EntityState.Detached;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string? ExtensionOf(string name)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext) || ext.Length < 2)
            return null;
        ext = ext[1..].ToLowerInvariant();
        return ext.Length > 32 ? ext[..32] : ext;
    }
}
