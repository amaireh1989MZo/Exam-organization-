using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Data;
using QuestionBank.Core.Extraction;

namespace QuestionBank.Core.Review;

/// <summary>
/// سجل تدقيق: من فعل ماذا ومتى، والقيمة قبل وبعد. لا يحتوي نصوص الأسئلة؛ يشير إليها بالمعرّف فقط.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public required string User { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public long EntityId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

public static class AuditActions
{
    public const string SetAnswer = "SetAnswer";
    public const string RevertAnswer = "RevertAnswer";
    public const string Approve = "Approve";
    public const string Flag = "Flag";
    public const string Reopen = "Reopen";
}

public enum ReviewOutcome
{
    Ok,
    NotFound,
    InvalidOption,
    NoAnswer,
    NotAllowedInStatus
}

public sealed record ReviewItem(
    long Id, string StableKey, int Order, string? Code, string Text, IReadOnlyList<string> Options,
    int? OriginalCorrectIndex, int? CorrectedIndex, int? EffectiveCorrectIndex,
    string Detector, string Confidence, string Location, IReadOnlyList<string> Issues,
    string ReviewStatus, string Source, string? ReviewedBy, DateTime? ReviewedAtUtc);

public sealed record ReviewPage(IReadOnlyDictionary<string, int> Counts, int Total, IReadOnlyList<ReviewItem> Items);

/// <summary>
/// المراجعة: تصحيح الإجابة كطبقة منفصلة فوق الأصل، والاعتماد، والتعليم للجنة، وإعادة الفتح.
/// كل عملية تُكتب في سجل التدقيق في المعاملة نفسها.
/// </summary>
public sealed class ReviewService(QuestionBankDbContext db, TimeProvider time)
{
    public async Task<ReviewPage> ListAsync(QuestionReviewStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        var counts = await db.Questions.AsNoTracking()
            .GroupBy(q => q.ReviewStatus)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key.ToString(), x => x.Count, cancellationToken);

        var query = db.Questions.AsNoTracking().AsQueryable();
        if (status is not null)
            query = query.Where(q => q.ReviewStatus == status);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(q => q.Confidence == ExtractionConfidence.Low ? 0 : q.Confidence == ExtractionConfidence.Medium ? 1 : 2)
            .ThenBy(q => q.Id)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200))
            .Select(q => new
            {
                Q = q,
                File = db.SourceFiles.Where(f => f.Id == q.SourceFileId).Select(f => f.RelativePath).FirstOrDefault(),
                Upload = db.Uploads.Where(u => u.Id == q.UploadId).Select(u => u.ClientPath ?? u.OriginalName).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return new ReviewPage(counts, total, rows.Select(r => ToItem(r.Q, r.File ?? ("رفع يدوي: " + r.Upload))).ToList());
    }

    /// <summary>يعيّن الإجابة الصحيحة كتصحيح. الإجابة الأصلية لا تتغير. null يلغي التصحيح ويعيد الأصل.</summary>
    public Task<ReviewOutcome> SetAnswerAsync(long questionId, int? index, string user, CancellationToken cancellationToken) =>
        MutateAsync(questionId, user, cancellationToken, q =>
        {
            if (q.ReviewStatus == QuestionReviewStatus.Approved)
                return (ReviewOutcome.NotAllowedInStatus, null, null, null);
            var options = JsonSerializer.Deserialize<string[]>(q.OriginalOptionsJson) ?? [];
            if (index is not null && (index < 0 || index >= options.Length))
                return (ReviewOutcome.InvalidOption, null, null, null);

            var old = q.CorrectedIndex;
            // تعيين الأصل نفسه يعني لا تصحيح.
            q.CorrectedIndex = index == q.OriginalCorrectIndex ? null : index;
            var action = index is null ? AuditActions.RevertAnswer : AuditActions.SetAnswer;
            return (ReviewOutcome.Ok, action, Effective(old, q.OriginalCorrectIndex)?.ToString(), Effective(q.CorrectedIndex, q.OriginalCorrectIndex)?.ToString());
        });

    public Task<ReviewOutcome> ApproveAsync(long questionId, string user, CancellationToken cancellationToken) =>
        MutateAsync(questionId, user, cancellationToken, q =>
        {
            if (Effective(q.CorrectedIndex, q.OriginalCorrectIndex) is null)
                return (ReviewOutcome.NoAnswer, null, null, null);
            if (q.ReviewStatus == QuestionReviewStatus.Approved)
                return (ReviewOutcome.Ok, null, null, null);
            var old = q.ReviewStatus.ToString();
            q.ReviewStatus = QuestionReviewStatus.Approved;
            return (ReviewOutcome.Ok, AuditActions.Approve, old, q.ReviewStatus.ToString());
        });

    public Task<ReviewOutcome> FlagAsync(long questionId, string user, CancellationToken cancellationToken) =>
        MutateAsync(questionId, user, cancellationToken, q =>
        {
            if (q.ReviewStatus == QuestionReviewStatus.Flagged)
                return (ReviewOutcome.Ok, null, null, null);
            var old = q.ReviewStatus.ToString();
            q.ReviewStatus = QuestionReviewStatus.Flagged;
            return (ReviewOutcome.Ok, AuditActions.Flag, old, q.ReviewStatus.ToString());
        });

    /// <summary>يعيد السؤال إلى قائمة المراجعة (تراجع عن اعتماد أو تعليم).</summary>
    public Task<ReviewOutcome> ReopenAsync(long questionId, string user, CancellationToken cancellationToken) =>
        MutateAsync(questionId, user, cancellationToken, q =>
        {
            if (q.ReviewStatus is QuestionReviewStatus.AwaitingApproval or QuestionReviewStatus.NeedsReview)
                return (ReviewOutcome.Ok, null, null, null);
            var old = q.ReviewStatus.ToString();
            q.ReviewStatus = q.Confidence == ExtractionConfidence.High && Effective(q.CorrectedIndex, q.OriginalCorrectIndex) is not null
                ? QuestionReviewStatus.AwaitingApproval
                : QuestionReviewStatus.NeedsReview;
            return (ReviewOutcome.Ok, AuditActions.Reopen, old, q.ReviewStatus.ToString());
        });

    public async Task<IReadOnlyList<AuditEntry>> HistoryAsync(long questionId, CancellationToken cancellationToken) =>
        await db.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == nameof(Question) && a.EntityId == questionId)
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);

    private async Task<ReviewOutcome> MutateAsync(
        long questionId, string user, CancellationToken cancellationToken,
        Func<Question, (ReviewOutcome Outcome, string? Action, string? Old, string? New)> change)
    {
        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == questionId, cancellationToken);
        if (q is null)
            return ReviewOutcome.NotFound;

        var (outcome, action, oldValue, newValue) = change(q);
        if (outcome != ReviewOutcome.Ok || action is null)
        {
            db.Entry(q).State = EntityState.Detached;
            return outcome;
        }

        var now = time.GetUtcNow().UtcDateTime;
        q.ReviewedBy = user;
        q.ReviewedAtUtc = now;
        db.AuditLog.Add(new AuditEntry
        {
            AtUtc = now,
            User = user,
            Action = action,
            EntityType = nameof(Question),
            EntityId = q.Id,
            OldValue = oldValue,
            NewValue = newValue
        });
        await db.SaveChangesAsync(cancellationToken); // معاملة واحدة: التغيير وسجله معًا
        db.ChangeTracker.Clear();
        return ReviewOutcome.Ok;
    }

    private static int? Effective(int? corrected, int? original) => corrected ?? original;

    private static ReviewItem ToItem(Question q, string source) => new(
        q.Id, q.StableKey, q.OriginalOrder, q.OriginalCode, q.OriginalText,
        JsonSerializer.Deserialize<string[]>(q.OriginalOptionsJson) ?? [],
        q.OriginalCorrectIndex, q.CorrectedIndex, Effective(q.CorrectedIndex, q.OriginalCorrectIndex),
        q.AnswerDetector, q.Confidence.ToString(), q.Location,
        q.IssuesJson is null ? [] : JsonSerializer.Deserialize<string[]>(q.IssuesJson) ?? [],
        q.ReviewStatus.ToString(), source, q.ReviewedBy, q.ReviewedAtUtc);
}
