namespace QuestionBank.Core.Inventory;

/// <summary>حالة الملف في دورة المعالجة. الجرد يضع الملفات الجديدة أو المتغيرة في Pending فقط.</summary>
public enum FileStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    Skipped,
    NeedsReview
}

public enum ScanRunStatus
{
    Running,
    Completed,
    Cancelled,
    Failed
}

public enum ScanDirectoryStatus
{
    Pending,
    Completed,
    Failed
}

/// <summary>ملف مكتشف في جذر المصدر. لا يُحذف السجل أبدًا؛ الملف المختفي يُعلَّم IsMissing.</summary>
public sealed class SourceFile
{
    public long Id { get; set; }

    /// <summary>المسار النسبي من جذر المصدر بفاصل '/' دائمًا، مهما كان نظام التشغيل.</summary>
    public required string RelativePath { get; set; }

    public string? Extension { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }

    /// <summary>بصمة المحتوى. لا يحسبها الجرد (مكلفة على SMB)؛ تُحسب عند الاستخراج.</summary>
    public string? ContentHash { get; set; }

    public FileStatus Status { get; set; } = FileStatus.Pending;
    public int Attempts { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime? LastErrorAtUtc { get; set; }
    public DateTime DiscoveredAtUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public long LastSeenRunId { get; set; }

    /// <summary>عدد مرات اكتشاف تغيّر الملف (الحجم أو تاريخ التعديل) بعد أول جرد.</summary>
    public int ChangeCount { get; set; }

    public bool IsMissing { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}

/// <summary>عملية جرد واحدة. العملية غير المكتملة تُستأنف عند إعادة التشغيل.</summary>
public sealed class ScanRun
{
    public long Id { get; set; }
    public ScanRunStatus Status { get; set; } = ScanRunStatus.Running;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public long DirectoriesCompleted { get; set; }
    public long DirectoriesFailed { get; set; }
    public long FilesSeen { get; set; }
    public long FilesNew { get; set; }
    public long FilesChanged { get; set; }
    public long FilesMissing { get; set; }
    public long LinksSkipped { get; set; }
    public long EntryErrors { get; set; }
    public string? FailureCode { get; set; }
}

/// <summary>
/// مجلد ضمن طابور عملية جرد. الطابور محفوظ في قاعدة البيانات، فيستأنف الجرد من حيث توقف
/// بدل إعادة المرور على 23 ألف مجلد.
/// </summary>
public sealed class ScanDirectory
{
    public long Id { get; set; }
    public long RunId { get; set; }

    /// <summary>المسار النسبي للمجلد. الجذر نفسه هو النص الفارغ.</summary>
    public required string RelativePath { get; set; }

    public ScanDirectoryStatus Status { get; set; } = ScanDirectoryStatus.Pending;
    public int Attempts { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime NextAttemptUtc { get; set; }
}

/// <summary>رموز أخطاء ثابتة تُحفظ وتُسجَّل بدل رسائل الاستثناءات التي قد تحتوي مسارات.</summary>
public static class InventoryErrorCodes
{
    public const string AccessDenied = "AccessDenied";
    public const string NotFound = "NotFound";
    public const string IoError = "IoError";
    public const string PathTooLong = "PathTooLong";
    public const string OutsideRoot = "OutsideRoot";
    public const string SourceUnavailable = "SourceUnavailable";
    public const string Unexpected = "Unexpected";
}
