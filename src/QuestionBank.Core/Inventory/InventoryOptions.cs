using System.ComponentModel.DataAnnotations;

namespace QuestionBank.Core.Inventory;

/// <summary>إعدادات قسم QuestionBank في appsettings أو متغيرات البيئة (QuestionBank__SourceRoot...).</summary>
public sealed class InventoryOptions
{
    public const string SectionName = "QuestionBank";

    /// <summary>جذر المصدر، ويفضَّل مسار UNC مثل \\FILESERVER\QuestionBankReadOnly. يأتي من الإعدادات فقط وليس من المستخدم.</summary>
    [Required]
    public string SourceRoot { get; set; } = "";

    /// <summary>مجلد النتائج. يجب أن يكون منفصلًا عن المصدر وخارجه.</summary>
    [Required]
    public string OutputRoot { get; set; } = "";

    /// <summary>عدد المجلدات التي تُعالج قبل تحديث عدادات العملية.</summary>
    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 100;

    /// <summary>أقصى عدد محاولات لمجلد عند خطأ إدخال/إخراج مؤقت (انقطاع SMB مثلًا).</summary>
    [Range(1, 20)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>التأخير الأساسي قبل إعادة المحاولة؛ يتضاعف مع كل محاولة.</summary>
    [Range(0, 3600)]
    public int RetryBaseDelaySeconds { get; set; } = 5;

    /// <summary>بدء الجرد تلقائيًا عند تشغيل الخدمة. الافتراضي: لا، والجرد يبدأ بطلب صريح.</summary>
    public bool ScanOnStartup { get; set; }
}
