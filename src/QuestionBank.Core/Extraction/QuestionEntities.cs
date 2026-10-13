using QuestionBank.Core.Inventory;

namespace QuestionBank.Core.Extraction;

public enum QuestionReviewStatus
{
    /// <summary>ثقة عالية: بانتظار اعتماد سريع.</summary>
    AwaitingApproval,

    /// <summary>ثقة متوسطة أو منخفضة: يحتاج مراجعة وتصحيحًا.</summary>
    NeedsReview,

    Approved,
    Flagged
}

/// <summary>
/// سؤال مستخرج. الحقول Original* هي ما ورد في الملف ولا تتغير أبدًا؛ التصحيحات ستكون طبقة منفصلة (مرحلة المراجعة).
/// </summary>
public sealed class Question
{
    public long Id { get; set; }

    /// <summary>بصمة (بصمة محتوى الملف + ترتيب السؤال): ثابتة لنفس المحتوى مهما أُعيد الاستخراج.</summary>
    public required string StableKey { get; set; }

    public long? SourceFileId { get; set; }
    public long? UploadId { get; set; }
    public required string ContentHash { get; set; }

    public int OriginalOrder { get; set; }
    public string? OriginalCode { get; set; }
    public required string OriginalText { get; set; }
    public required string OriginalOptionsJson { get; set; }
    public int? OriginalCorrectIndex { get; set; }

    public required string AnswerDetector { get; set; }
    public ExtractionConfidence Confidence { get; set; }
    public required string Location { get; set; }
    public string? IssuesJson { get; set; }

    public required string ReaderName { get; set; }
    public required string ReaderVersion { get; set; }
    public DateTime ExtractedAtUtc { get; set; }
    public QuestionReviewStatus ReviewStatus { get; set; }
}

/// <summary>ملف رفعه مستخدم من المتصفح. يُحفظ في مجلد النتائج باسم بصمته، وليس في المصدر.</summary>
public sealed class Upload
{
    public long Id { get; set; }

    /// <summary>الاسم الأصلي كما أرسله المتصفح (للعرض فقط، لا يُستخدم في أي مسار).</summary>
    public required string OriginalName { get; set; }

    /// <summary>المسار داخل المجلد المسحوب، إن سُحب مجلد (للعرض فقط).</summary>
    public string? ClientPath { get; set; }

    public string? Extension { get; set; }
    public long SizeBytes { get; set; }
    public required string Sha256 { get; set; }
    public required string StoredName { get; set; }
    public required string UploadedBy { get; set; }
    public DateTime UploadedAtUtc { get; set; }
    public FileStatus Status { get; set; } = FileStatus.Pending;
    public string? LastErrorCode { get; set; }
    public int QuestionCount { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}
