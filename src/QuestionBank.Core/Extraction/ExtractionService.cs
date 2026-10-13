using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Data;

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

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Questions
            .Where(q => (sourceFileId != null && q.SourceFileId == sourceFileId) || (uploadId != null && q.UploadId == uploadId))
            .ExecuteDeleteAsync(cancellationToken);

        var added = new List<Question>(result.Questions.Count);
        foreach (var q in result.Questions)
        {
            var entity = new Question
            {
                StableKey = StableKey(sourceFileId is not null ? $"f{sourceFileId}" : $"u{uploadId}", hash, q.Order),
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
        await tx.CommitAsync(cancellationToken);
        // نفصل الأسئلة فقط حتى لا تكبر الذاكرة؛ كيانات المستدعي (الملف، الطلب) تبقى متتبَّعة.
        foreach (var entity in added)
            db.Entry(entity).State = EntityState.Detached;

        return new ExtractionOutcome(true, result.Questions.Count, result.Warnings.Count, null);
    }

    /// <summary>
    /// المعرّف الثابت مبني على المصدر وبصمة المحتوى وترتيب السؤال: ثابت لنفس المحتوى مهما أُعيد الاستخراج،
    /// ويتغير إذا تغيّر الملف. نسختان متطابقتان في مجلدين تبقيان سؤالين لكل منهما مصدره،
    /// والتكرار بينهما يكشفه فحص الجودة (ContentHash متساوٍ) ولا يُحذف تلقائيًا.
    /// </summary>
    public static string StableKey(string source, string contentHash, int order) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}:{contentHash}:{order}")))[..32];
}
