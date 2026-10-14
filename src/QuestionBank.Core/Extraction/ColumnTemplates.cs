using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Data;

namespace QuestionBank.Core.Extraction;

/// <summary>
/// قالب مطابقة أعمدة Excel يعلّمه المستخدم مرة واحدة: صف عناوين معين (بترتيبه) وأي عمود يعني ماذا.
/// يُطبَّق تلقائيًا على كل ملف له صف العناوين نفسه.
/// </summary>
public sealed class ColumnTemplate
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string HeaderKey { get; set; }
    public required string HeadersJson { get; set; }
    public required string StemHeader { get; set; }
    public required string OptionHeadersJson { get; set; }
    public string? AnswerHeader { get; set; }
    public string? CodeHeader { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed record ColumnTemplateMap(string HeaderKey, string Stem, IReadOnlyList<string> Options, string? Answer, string? Code);

public sealed record PreviewRow(int RowNo, IReadOnlyList<string> Cells);

public sealed record SheetPreview(string Name, IReadOnlyList<PreviewRow> Rows);

public sealed record TemplateRequest(string Name, IReadOnlyList<string> Headers, string Stem, IReadOnlyList<string> Options, string? Answer, string? Code);

public sealed record TemplateView(long Id, string Name, IReadOnlyList<string> Headers, string Stem, IReadOnlyList<string> Options,
    string? Answer, string? Code, string CreatedBy, DateTime CreatedAtUtc);

public sealed class ColumnTemplateService(QuestionBankDbContext db, TimeProvider time)
{
    public async Task<IReadOnlyList<ColumnTemplateMap>> MapsAsync(CancellationToken cancellationToken) =>
        (await db.ColumnTemplates.AsNoTracking().ToListAsync(cancellationToken))
        .Select(t => new ColumnTemplateMap(t.HeaderKey, t.StemHeader,
            JsonSerializer.Deserialize<string[]>(t.OptionHeadersJson) ?? [], t.AnswerHeader, t.CodeHeader))
        .ToList();

    public async Task<IReadOnlyList<TemplateView>> ListAsync(CancellationToken cancellationToken) =>
        (await db.ColumnTemplates.AsNoTracking().OrderByDescending(t => t.Id).ToListAsync(cancellationToken))
        .Select(ToView).ToList();

    /// <summary>يحفظ القالب أو يحدّثه إذا وُجد قالب لصف العناوين نفسه. يعيد null مع رسالة إذا كان الطلب غير صالح.</summary>
    public async Task<(TemplateView? Template, string? Error)> SaveAsync(TemplateRequest request, string user, CancellationToken cancellationToken)
    {
        var headers = request.Headers.Select(h => h ?? "").ToList();
        var present = headers.Where(h => h.Trim().Length > 0).ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.Name))
            return (null, "أدخل اسمًا للقالب.");
        if (!present.Contains(request.Stem))
            return (null, "حدّد عمود نص السؤال من عناوين الصف.");
        var options = request.Options.Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
        if (options.Count < 2 || options.Any(o => !present.Contains(o)))
            return (null, "حدّد عمودين على الأقل للخيارات من عناوين الصف.");
        if (request.Answer is { Length: > 0 } a && !present.Contains(a))
            return (null, "عمود الإجابة ليس من عناوين الصف.");
        if (request.Code is { Length: > 0 } c && !present.Contains(c))
            return (null, "عمود الرمز ليس من عناوين الصف.");
        var used = new[] { request.Stem, request.Answer, request.Code }.Concat(options).Where(x => !string.IsNullOrEmpty(x)).ToList();
        if (used.Count != used.Distinct().Count())
            return (null, "لا يجوز استخدام العمود نفسه لأكثر من حقل.");

        // المفتاح من الخلايا غير الفارغة بترتيبها، كما يقرأها القارئ.
        var key = XlsxReader.HeaderKey(headers.Where(h => h.Trim().Length > 0));
        var t = await db.ColumnTemplates.FirstOrDefaultAsync(x => x.HeaderKey == key, cancellationToken);
        if (t is null)
        {
            t = new ColumnTemplate
            {
                Name = "", HeaderKey = key, HeadersJson = "", StemHeader = "", OptionHeadersJson = "",
                CreatedBy = user, CreatedAtUtc = time.GetUtcNow().UtcDateTime
            };
            db.ColumnTemplates.Add(t);
        }
        t.Name = request.Name.Trim();
        t.HeadersJson = JsonSerializer.Serialize(headers, ExtractionService.Json);
        t.StemHeader = request.Stem;
        t.OptionHeadersJson = JsonSerializer.Serialize(options, ExtractionService.Json);
        t.AnswerHeader = string.IsNullOrEmpty(request.Answer) ? null : request.Answer;
        t.CodeHeader = string.IsNullOrEmpty(request.Code) ? null : request.Code;
        db.AuditLog.Add(new Review.AuditEntry
        {
            AtUtc = time.GetUtcNow().UtcDateTime, User = user, Action = "SaveColumnTemplate",
            EntityType = nameof(ColumnTemplate), EntityId = t.Id, NewValue = t.Name.Length > 200 ? t.Name[..200] : t.Name
        });
        await db.SaveChangesAsync(cancellationToken);
        return (ToView(t), null);
    }

    /// <summary>
    /// ملفات المصدر التي توقفت بانتظار مطابقة أعمدة تعود إلى Pending، وتُطلب مجلداتها في الطابور
    /// (مجلداتها فقط، وليس البنك كله). يعيد عدد الملفات.
    /// </summary>
    public async Task<int> RequeueUnmappedAsync(string user, CancellationToken cancellationToken)
    {
        var files = await db.SourceFiles
            .Where(f => f.Status == Inventory.FileStatus.NeedsReview && f.LastErrorCode == UploadService.NeedsColumnMapping)
            .ToListAsync(cancellationToken);
        if (files.Count == 0)
            return 0;
        foreach (var f in files)
        {
            f.Status = Inventory.FileStatus.Pending;
            f.LastErrorCode = null;
        }
        var folders = files.Select(f => f.RelativePath.Contains('/') ? f.RelativePath[..f.RelativePath.LastIndexOf('/')] : "").Distinct().ToList();
        var active = await db.ProcessingQueue
            .Where(q => q.Status == Inventory.QueueItemStatus.Queued || q.Status == Inventory.QueueItemStatus.Processing)
            .Select(q => q.RelativePath).ToListAsync(cancellationToken);
        foreach (var folder in folders.Except(active))
        {
            db.ProcessingQueue.Add(new Inventory.ProcessingQueueItem
            {
                RelativePath = folder, RequestedBy = user, RequestedAtUtc = time.GetUtcNow().UtcDateTime,
                FileCount = files.Count(f => (f.RelativePath.Contains('/') ? f.RelativePath[..f.RelativePath.LastIndexOf('/')] : "") == folder)
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        return files.Count;
    }

    private static TemplateView ToView(ColumnTemplate t) => new(t.Id, t.Name,
        JsonSerializer.Deserialize<string[]>(t.HeadersJson) ?? [], t.StemHeader,
        JsonSerializer.Deserialize<string[]>(t.OptionHeadersJson) ?? [], t.AnswerHeader, t.CodeHeader, t.CreatedBy, t.CreatedAtUtc);
}
