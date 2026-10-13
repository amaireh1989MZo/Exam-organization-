namespace QuestionBank.Core.Frameworks;

/// <summary>إطار كفايات مستورد من ملف المؤسسة. الإطار المعتمد هو المرجع الوحيد للتصنيف.</summary>
public sealed class CompetencyFramework
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string Version { get; set; }
    public required string SourceName { get; set; }
    public required string Sha256 { get; set; }
    public required string ImportedBy { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

/// <summary>
/// مؤشر كما ورد في ملف الإطار حرفيًا: لا يُعاد تسميته ولا يُنشأ من الواجهة (القيد 3 في الحزمة).
/// </summary>
public sealed class CompetencyIndicator
{
    public long Id { get; set; }
    public long FrameworkId { get; set; }
    public int Order { get; set; }
    public required string MainDomain { get; set; }
    public string? SubDomain { get; set; }
    public string? Code { get; set; }
    public required string Name { get; set; }
    public required string SourceRow { get; set; }
}

public enum MatchStatus
{
    /// <summary>لا ربط.</summary>
    None,

    /// <summary>ربطه إنسان (بالسحب أو باختيار اقتراح).</summary>
    Matched
}

/// <summary>مؤشر مقترح لسؤال، مع الدرجة والمبرر. الاقتراح لا يُطبَّق تلقائيًا أبدًا.</summary>
public sealed record IndicatorSuggestion(long IndicatorId, string? Code, string Name, double Score, string Method, string Rationale);

/// <summary>
/// مزوّد الاقتراحات. التنفيذ الحالي نصي (تشابه المقاطع)؛ نموذج التضمين المحلي يحل محله لاحقًا عبر الواجهة نفسها.
/// </summary>
public interface IIndicatorSuggester
{
    string Method { get; }
    IReadOnlyList<IndicatorSuggestion> Suggest(string questionText, IReadOnlyList<string> options, IReadOnlyList<CompetencyIndicator> indicators, int top);
}
