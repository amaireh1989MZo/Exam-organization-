namespace QuestionBank.Core.Extraction;

/// <summary>درجة الثقة في استخراج السؤال، وأهمها ثقة تحديد الإجابة الصحيحة.</summary>
public enum ExtractionConfidence
{
    /// <summary>إجابة محددة بطريقة صريحة واحدة على الأقل، دون تعارض.</summary>
    High,

    /// <summary>إجابة محددة بطريقة أضعف (تنسيق مثلًا) أو بنية غير مكتملة.</summary>
    Medium,

    /// <summary>لا إجابة، أو تعارض بين طريقتين، أو بنية ناقصة. يُحال للمراجع.</summary>
    Low
}

/// <summary>طرق تحديد الإجابة الصحيحة. تُحفظ مع كل سؤال لتتبع مصدر القرار.</summary>
public static class AnswerDetectors
{
    public const string Gift = "gift";
    public const string Marker = "marker";
    public const string AnswerLine = "answer-line";
    public const string AnswerKey = "answer-key";
    public const string None = "none";
    public const string Conflict = "conflict";
}

/// <summary>
/// صورة من الملف كما هي (بلا تحويل). OptionIndex = null للصورة في نص السؤال، أو رقم الخيار الذي تتبعه.
/// </summary>
public sealed record ExtractedImage(byte[] Content, string ContentType, int? OptionIndex = null)
{
    /// <summary>الصيغ التي يعرضها المتصفح وMoodle مباشرة. غيرها (EMF وWMF وTIFF) يُحفظ لكنه يحتاج تحويلًا.</summary>
    public static bool IsDisplayable(string contentType) =>
        contentType is "image/png" or "image/jpeg" or "image/gif" or "image/bmp" or "image/webp";

    /// <summary>الحد الأعلى لحجم الصورة الواحدة. الأكبر لا يُحفظ ويُحال السؤال للمراجع.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    public static string Extension(string contentType) => contentType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/bmp" => "bmp",
        "image/webp" => "webp",
        "image/x-emf" or "image/emf" => "emf",
        "image/x-wmf" or "image/wmf" => "wmf",
        "image/tiff" => "tif",
        _ => "bin"
    };
}

/// <summary>
/// سؤال كما ورد في الملف. النص والخيارات وترتيبها محفوظة كما هي (بعد إزالة رموز الترقيم والعلامات فقط)،
/// ولا يُعاد صياغة أي شيء.
/// </summary>
public sealed record ExtractedQuestion(
    int Order,
    string? Code,
    string Text,
    IReadOnlyList<string> Options,
    int? CorrectIndex,
    string Detector,
    ExtractionConfidence Confidence,
    string Location,
    IReadOnlyList<string> Issues,
    IReadOnlyList<ExtractedImage>? Images = null);

public sealed record ReaderResult(string ReaderName, string ReaderVersion, IReadOnlyList<ExtractedQuestion> Questions, IReadOnlyList<string> Warnings);

public interface IQuestionReader
{
    string Name { get; }
    string Version { get; }
    bool CanRead(string extension);
    ReaderResult Read(byte[] content);
}

/// <summary>قائمة القارئات المتاحة. الصيغ غير المدعومة بعد تبقى بانتظار قارئها.</summary>
public sealed class ReaderRegistry(IEnumerable<IQuestionReader> readers)
{
    private readonly IReadOnlyList<IQuestionReader> _readers = readers.ToList();

    public IQuestionReader? For(string? extension) =>
        extension is null ? null : _readers.FirstOrDefault(r => r.CanRead(extension.ToLowerInvariant()));

    public IReadOnlyList<string> SupportedExtensions =>
        new[] { "txt", "md", "gift", "docx", "xlsx", "xls", "doc", "pdf" }.Where(e => For(e) is not null).ToList();

    public static ReaderRegistry Default() => new([new GiftReader(), new TextQuestionReader(), new DocxReader(), new XlsxReader()]);
}
