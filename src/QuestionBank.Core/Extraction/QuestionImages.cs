using Microsoft.EntityFrameworkCore;

namespace QuestionBank.Core.Extraction;

/// <summary>
/// محتوى صورة كما ورد في الملف، مرة واحدة لكل بصمة SHA-256 مهما تكررت في الأسئلة.
/// يُحفظ في قاعدة البيانات حتى يُستبدل مع أسئلته في المعاملة نفسها، ويدخل في النسخ الاحتياطي.
/// </summary>
public sealed class ImageBlob
{
    public required string Sha256 { get; set; }
    public required string ContentType { get; set; }
    public int SizeBytes { get; set; }
    public required byte[] Content { get; set; }
}

/// <summary>ربط صورة بسؤال: في نص السؤال (OptionIndex = null) أو في خيار، بترتيب ظهورها.</summary>
public sealed class QuestionImage
{
    public long Id { get; set; }
    public long QuestionId { get; set; }
    public int? OptionIndex { get; set; }
    public int Order { get; set; }
    public required string Sha256 { get; set; }
}

/// <summary>إشارة إلى صورة سؤال للعرض: البصمة تُستخدم في رابط الصورة، ولا يُرسل المحتوى مع القائمة.</summary>
public sealed record ImageRef(string Sha256, int? OptionIndex, string ContentType, bool Displayable);

public static class QuestionImageQueries
{
    /// <summary>صور مجموعة أسئلة بترتيبها، مجمّعة حسب السؤال.</summary>
    public static async Task<Dictionary<long, List<ImageRef>>> RefsAsync(
        Data.QuestionBankDbContext db, IReadOnlyCollection<long> questionIds, CancellationToken cancellationToken)
    {
        if (questionIds.Count == 0)
            return [];
        var ids = questionIds.ToList();
        var rows = await (from qi in db.QuestionImages.AsNoTracking()
                          join b in db.ImageBlobs.AsNoTracking() on qi.Sha256 equals b.Sha256
                          where ids.Contains(qi.QuestionId)
                          orderby qi.QuestionId, qi.Order
                          select new { qi.QuestionId, qi.Sha256, qi.OptionIndex, b.ContentType })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.QuestionId).ToDictionary(g => g.Key,
            g => g.Select(r => new ImageRef(r.Sha256, r.OptionIndex, r.ContentType, ExtractedImage.IsDisplayable(r.ContentType))).ToList());
    }
}
