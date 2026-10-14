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
    public const string SetText = "SetText";
    public const string RevertText = "RevertText";
}

public enum ReviewOutcome
{
    Ok,
    NotFound,
    InvalidOption,
    NoAnswer,
    NotAllowedInStatus,
    InvalidText
}

/// <summary>Text وOptions هما النص الفعّال (المصحّح إن وُجد)؛ الأصل في OriginalText وOriginalOptions.</summary>
public sealed record ReviewItem(
    long Id, string StableKey, int Order, string? Code, string Text, IReadOnlyList<string> Options,
    int? OriginalCorrectIndex, int? CorrectedIndex, int? EffectiveCorrectIndex,
    string Detector, string Confidence, string Location, IReadOnlyList<string> Issues,
    string ReviewStatus, string Source, string? ReviewedBy, DateTime? ReviewedAtUtc,
    bool TextCorrected = false, string? OriginalText = null, IReadOnlyList<string>? OriginalOptions = null);

public sealed record ReviewPage(IReadOnlyDictionary<string, int> Counts, int Total, IReadOnlyList<ReviewItem> Items);

public sealed record AssignmentRequest(string Reviewer, string? Folder, int Count);

public sealed record ReviewerProgress(string Reviewer, int Assigned, int Open, int Approved, int Flagged);

/// <summary>
/// المراجعة: تصحيح الإجابة كطبقة منفصلة فوق الأصل، والاعتماد، والتعليم للجنة، وإعادة الفتح.
/// كل عملية تُكتب في سجل التدقيق في المعاملة نفسها.
/// </summary>
public sealed class ReviewService(QuestionBankDbContext db, TimeProvider time)
{
    public async Task<ReviewPage> ListAsync(QuestionReviewStatus? status, int skip, int take, CancellationToken cancellationToken, string? assignedTo = null)
    {
        var scope = db.Questions.AsNoTracking().AsQueryable();
        if (assignedTo is not null)
            scope = scope.Where(q => q.AssignedTo == assignedTo);
        var counts = await scope
            .GroupBy(q => q.ReviewStatus)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key.ToString(), x => x.Count, cancellationToken);

        var query = scope;
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

    /// <summary>
    /// يصحح نص السؤال والخيارات كطبقة فوق الأصل. عدد الخيارات وترتيبها ثابتان (الإجابة مرتبطة بموقعها).
    /// تمرير null لكليهما يلغي التصحيح. السجل يذكر الحقول المعدّلة فقط وليس النص.
    /// </summary>
    public Task<ReviewOutcome> SetTextAsync(long questionId, string? text, IReadOnlyList<string>? options, string user, CancellationToken cancellationToken) =>
        MutateAsync(questionId, user, cancellationToken, q =>
        {
            if (q.ReviewStatus is QuestionReviewStatus.Approved or QuestionReviewStatus.Superseded)
                return (ReviewOutcome.NotAllowedInStatus, null, null, null);
            var original = JsonSerializer.Deserialize<string[]>(q.OriginalOptionsJson) ?? [];
            if (text is null && options is null)
            {
                if (q.CorrectedText is null && q.CorrectedOptionsJson is null)
                    return (ReviewOutcome.Ok, null, null, null);
                q.CorrectedText = null;
                q.CorrectedOptionsJson = null;
                return (ReviewOutcome.Ok, AuditActions.RevertText, null, "original");
            }

            var newText = (text ?? q.EffectiveText).Trim();
            var newOptions = (options ?? JsonSerializer.Deserialize<string[]>(q.EffectiveOptionsJson) ?? []).Select(o => (o ?? "").Trim()).ToArray();
            if (newText.Length == 0 || newText.Length > 10_000 || newOptions.Length != original.Length
                || newOptions.Any(o => o.Length == 0 || o.Length > 2_000))
                return (ReviewOutcome.InvalidText, null, null, null);

            var changed = new List<string>();
            if (newText != q.OriginalText) changed.Add("text");
            changed.AddRange(newOptions.Select((o, i) => o != original[i] ? $"option{i + 1}" : null).OfType<string>());
            q.CorrectedText = newText == q.OriginalText ? null : newText;
            q.CorrectedOptionsJson = newOptions.SequenceEqual(original) ? null : JsonSerializer.Serialize(newOptions, Extraction.ExtractionService.Json);
            return (ReviewOutcome.Ok, changed.Count == 0 ? AuditActions.RevertText : AuditActions.SetText, null,
                changed.Count == 0 ? "original" : string.Join(",", changed));
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

    /// <summary>
    /// يسند حتى Count سؤالًا غير مسند ويحتاج مراجعة (تحتاج مراجعة أولًا ثم بانتظار الاعتماد)، من مجلد معين إن طُلب.
    /// يعيد عدد ما أُسند. يُسجَّل في سجل التدقيق.
    /// </summary>
    public async Task<int> AssignAsync(AssignmentRequest request, string user, CancellationToken cancellationToken)
    {
        var reviewer = request.Reviewer?.Trim() ?? "";
        if (reviewer.Length == 0 || reviewer.Length > 256 || request.Count <= 0)
            return 0;
        var prefix = string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder.Trim().TrimEnd('/') + "/";
        var candidates = db.Questions.Where(q => q.AssignedTo == null &&
            (q.ReviewStatus == QuestionReviewStatus.NeedsReview || q.ReviewStatus == QuestionReviewStatus.AwaitingApproval));
        if (prefix is not null)
            candidates = candidates.Where(q => db.SourceFiles.Any(f => f.Id == q.SourceFileId && f.RelativePath.StartsWith(prefix)));
        var picked = await candidates
            .OrderBy(q => q.ReviewStatus == QuestionReviewStatus.NeedsReview ? 0 : 1).ThenBy(q => q.Id)
            .Take(Math.Min(request.Count, 5000))
            .ToListAsync(cancellationToken);
        foreach (var q in picked)
            q.AssignedTo = reviewer;
        if (picked.Count > 0)
        {
            db.AuditLog.Add(new AuditEntry
            {
                AtUtc = time.GetUtcNow().UtcDateTime, User = user, Action = "Assign", EntityType = "ReviewBatch",
                EntityId = picked[0].Id, NewValue = $"{reviewer}:{picked.Count}"
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return picked.Count;
    }

    /// <summary>يلغي إسناد ما لم يُراجَع بعد لمراجع معين (المعتمد والمعلَّم يبقيان باسمه للتتبع).</summary>
    public async Task<int> UnassignOpenAsync(string reviewer, string user, CancellationToken cancellationToken)
    {
        var count = await db.Questions
            .Where(q => q.AssignedTo == reviewer && (q.ReviewStatus == QuestionReviewStatus.NeedsReview || q.ReviewStatus == QuestionReviewStatus.AwaitingApproval))
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.AssignedTo, (string?)null), cancellationToken);
        if (count > 0)
        {
            db.AuditLog.Add(new AuditEntry
            {
                AtUtc = time.GetUtcNow().UtcDateTime, User = user, Action = "Unassign", EntityType = "ReviewBatch", EntityId = 0,
                OldValue = $"{reviewer}:{count}"
            });
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        return count;
    }

    public async Task<IReadOnlyList<ReviewerProgress>> ProgressAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Questions.AsNoTracking().Where(q => q.AssignedTo != null)
            .GroupBy(q => new { q.AssignedTo, q.ReviewStatus })
            .Select(g => new { g.Key.AssignedTo, g.Key.ReviewStatus, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.AssignedTo!)
            .Select(g => new ReviewerProgress(g.Key, g.Sum(r => r.Count),
                g.Where(r => r.ReviewStatus is QuestionReviewStatus.NeedsReview or QuestionReviewStatus.AwaitingApproval).Sum(r => r.Count),
                g.Where(r => r.ReviewStatus == QuestionReviewStatus.Approved).Sum(r => r.Count),
                g.Where(r => r.ReviewStatus == QuestionReviewStatus.Flagged).Sum(r => r.Count)))
            .OrderBy(p => p.Reviewer, StringComparer.Ordinal)
            .ToList();
    }

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

    private static ReviewItem ToItem(Question q, string source)
    {
        var corrected = q.CorrectedText is not null || q.CorrectedOptionsJson is not null;
        return new(
            q.Id, q.StableKey, q.OriginalOrder, q.OriginalCode, q.EffectiveText,
            JsonSerializer.Deserialize<string[]>(q.EffectiveOptionsJson) ?? [],
            q.OriginalCorrectIndex, q.CorrectedIndex, Effective(q.CorrectedIndex, q.OriginalCorrectIndex),
            q.AnswerDetector, q.Confidence.ToString(), q.Location,
            q.IssuesJson is null ? [] : JsonSerializer.Deserialize<string[]>(q.IssuesJson) ?? [],
            q.ReviewStatus.ToString(), source, q.ReviewedBy, q.ReviewedAtUtc,
            corrected, corrected ? q.OriginalText : null,
            corrected ? JsonSerializer.Deserialize<string[]>(q.OriginalOptionsJson) : null);
    }
}
