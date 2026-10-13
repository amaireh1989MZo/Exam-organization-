using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Data;
using QuestionBank.Core.Review;

namespace QuestionBank.Core.Extraction;

public sealed record ExtractionOutcome(bool Supported, int QuestionCount, int Warnings, string? ErrorCode);

/// <summary>
/// يشغّل القارئ المناسب ويحفظ الأسئلة. إعادة الاستخراج لنفس المصدر تستبدل أسئلته في معاملة واحدة،
/// والمعرّف الثابت لا يتغير ما دام المحتوى لم يتغير.
/// </summary>
public sealed class ExtractionService(QuestionBankDbContext db, ReaderRegistry readers, TimeProvider time)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };

    public bool IsSupported(string? extension) => readers.For(extension) is not null;

    /// <summary>يستخرج من محتوى جاهز ويحفظ النتيجة مربوطة بملف مصدر أو ملف مرفوع.</summary>
    public async Task<ExtractionOutcome> ExtractAsync(
        byte[] content, string? extension, long? sourceFileId, long? uploadId, CancellationToken cancellationToken)
    {
        if ((sourceFileId is null) == (uploadId is null))
            throw new ArgumentException("حدد مصدرًا واحدًا: ملف مصدر أو ملف مرفوع.");

        var reader = readers.For(extension);
        if (reader is null)
            return new ExtractionOutcome(false, 0, 0, null);

        ReaderResult result;
        try
        {
            result = reader.Read(content);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or DecoderFallbackException)
        {
            return new ExtractionOutcome(true, 0, 0, "ReaderError");
        }

        var hash = Convert.ToHexString(SHA256.HashData(content));
        var now = time.GetUtcNow().UtcDateTime;

        var bySource = db.Questions.Where(q =>
            (sourceFileId != null && q.SourceFileId == sourceFileId) || (uploadId != null && q.UploadId == uploadId));
        var existingHashes = await bySource.Where(q => q.ReviewStatus != QuestionReviewStatus.Superseded)
            .Select(q => q.ContentHash).Distinct().ToListAsync(cancellationToken);

        // المحتوى نفسه استُخرج سابقًا: لا شيء يتغير، فتبقى المراجعة والتصحيحات كما هي.
        if (existingHashes.Count == 1 && existingHashes[0] == hash)
            return new ExtractionOutcome(true, await bySource.CountAsync(q => q.ContentHash == hash, cancellationToken), result.Warnings.Count, null);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        // المحتوى تغيّر: غير المراجَع يُحذف، والمراجَع يبقى «مُستبدَلًا» مع سجله، فلا يضيع عمل المراجعين.
        var superseded = await bySource
            .Where(q => q.ReviewedBy != null && q.ReviewStatus != QuestionReviewStatus.Superseded)
            .ToListAsync(cancellationToken);
        foreach (var old in superseded)
        {
            db.AuditLog.Add(new AuditEntry
            {
                AtUtc = now, User = "system", Action = "Supersede", EntityType = nameof(Question),
                EntityId = old.Id, OldValue = old.ReviewStatus.ToString(), NewValue = nameof(QuestionReviewStatus.Superseded)
            });
            old.ReviewStatus = QuestionReviewStatus.Superseded;
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var old in superseded)
            db.Entry(old).State = EntityState.Detached;
        await bySource.Where(q => q.ReviewedBy == null).ExecuteDeleteAsync(cancellationToken);

        // ملف عاد إلى محتوى قديم سبق أن استُبدلت أسئلته: نميّز المعرّف حتى لا يصطدم بالسؤال المستبدَل المحفوظ.
        var takenKeys = (await bySource.Select(q => q.StableKey).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var source = sourceFileId is not null ? $"f{sourceFileId}" : $"u{uploadId}";
        var added = new List<Question>(result.Questions.Count);
        foreach (var q in result.Questions)
        {
            var entity = new Question
            {
                StableKey = UniqueKey(source, hash, q.Order, takenKeys),
                SourceFileId = sourceFileId,
                UploadId = uploadId,
                ContentHash = hash,
                OriginalOrder = q.Order,
                OriginalCode = q.Code,
                OriginalText = q.Text,
                OriginalOptionsJson = JsonSerializer.Serialize(q.Options, Json),
                OriginalCorrectIndex = q.CorrectIndex,
                AnswerDetector = q.Detector,
                Confidence = q.Confidence,
                Location = q.Location,
                IssuesJson = q.Issues.Count == 0 ? null : JsonSerializer.Serialize(q.Issues, Json),
                StemHash = Quality.TextNormalizer.StemHash(q.Text),
                FullHash = Quality.TextNormalizer.FullHash(q.Text, q.Options),
                ReaderName = result.ReaderName,
                ReaderVersion = result.ReaderVersion,
                ExtractedAtUtc = now,
                ReviewStatus = q.Confidence == ExtractionConfidence.High && q.CorrectIndex is not null
                    ? QuestionReviewStatus.AwaitingApproval
                    : QuestionReviewStatus.NeedsReview
            };
            db.Questions.Add(entity);
            added.Add(entity);
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var entity in added)
            AddBands(db, entity);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        // نفصل الأسئلة فقط حتى لا تكبر الذاكرة؛ كيانات المستدعي (الملف، الطلب) تبقى متتبَّعة.
        foreach (var entity in added)
            db.Entry(entity).State = EntityState.Detached;
        foreach (var band in db.ChangeTracker.Entries<Quality.QuestionBand>().ToList())
            band.State = EntityState.Detached;

        return new ExtractionOutcome(true, result.Questions.Count, result.Warnings.Count, null);
    }

    /// <summary>
    /// المعرّف الثابت مبني على المصدر وبصمة المحتوى وترتيب السؤال: ثابت لنفس المحتوى مهما أُعيد الاستخراج،
    /// ويتغير إذا تغيّر الملف. نسختان متطابقتان في مجلدين تبقيان سؤالين لكل منهما مصدره،
    /// والتكرار بينهما يكشفه فحص الجودة (ContentHash متساوٍ) ولا يُحذف تلقائيًا.
    /// </summary>
    /// <summary>مفاتيح MinHash لشبه التكرار، على نص السؤال وحده.</summary>
    public static void AddBands(QuestionBankDbContext db, Question q)
    {
        var keys = Quality.NearDuplicates.BandKeys(q.OriginalText);
        for (var b = 0; b < keys.Count; b++)
            db.QuestionBands.Add(new Quality.QuestionBand { QuestionId = q.Id, Band = (byte)b, Key = keys[b] });
    }

    private static string UniqueKey(string source, string hash, int order, HashSet<string> taken)
    {
        var key = StableKey(source, hash, order);
        for (var n = 1; taken.Contains(key); n++)
            key = StableKey($"{source}#{n}", hash, order);
        taken.Add(key);
        return key;
    }

    public static string StableKey(string source, string contentHash, int order) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}:{contentHash}:{order}")))[..32];
}
