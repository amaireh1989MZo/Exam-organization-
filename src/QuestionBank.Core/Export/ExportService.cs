using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestionBank.Core.Data;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Inventory;
using QuestionBank.Core.Review;

namespace QuestionBank.Core.Export;

public enum ExportFormat
{
    Gift,
    MoodleXml
}

/// <summary>سجل عملية تصدير: الإعدادات ومن طلبها والنتيجة، لإعادة إنشائها أو تتبعها لاحقًا.</summary>
public sealed class ExportRecord
{
    public long Id { get; set; }
    public ExportFormat Format { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime AtUtc { get; set; }
    public int QuestionCount { get; set; }
    public int RejectedCount { get; set; }
    public required string FileName { get; set; }
    public required string SettingsJson { get; set; }
}

public sealed record ExportRequest(ExportFormat Format, IReadOnlyList<long> QuestionIds, bool AllowShuffle, bool CategoryBySource);

public sealed record ExportRejection(long QuestionId, string Reason);

public sealed record ExportResult(long? ExportId, string? FileName, int Exported, IReadOnlyList<ExportRejection> Rejected, string? ErrorCode);

/// <summary>
/// يصدّر الأسئلة المعتمدة فقط إلى GIFT أو Moodle XML. الملف يُفحص قبل حفظه:
/// GIFT يُعاد قراءته بقارئ GIFT ويُقارن، وXML يُحلَّل. الملف الذي لا يجتاز الفحص لا يُحفظ.
/// </summary>
public sealed class ExportService(
    QuestionBankDbContext db,
    IOptions<InventoryOptions> inventoryOptions,
    TimeProvider time)
{
    public const int MaxQuestionsPerExport = 5000;

    private sealed record Row(long Id, string StableKey, string? Code, string Text, string[] Options, int? Answer,
        QuestionReviewStatus Status, string Source, string Location, string? IndicatorCategory = null, string? IndicatorCode = null)
    {
        public IReadOnlyList<RowImage> Images { get; init; } = [];
    }

    private sealed record RowImage(int? OptionIndex, string Sha256, string ContentType, byte[] Content)
    {
        /// <summary>اسم الملف داخل Moodle: جزء من البصمة يكفي للتمييز داخل السؤال الواحد.</summary>
        public string FileName => Sha256[..16].ToLowerInvariant() + "." + ExtractedImage.Extension(ContentType);
    }

    public async Task<ExportResult> ExportAsync(ExportRequest request, string user, CancellationToken cancellationToken)
    {
        var ids = request.QuestionIds.Distinct().ToList();
        if (ids.Count == 0)
            return new ExportResult(null, null, 0, [], "Empty");
        if (ids.Count > MaxQuestionsPerExport)
            return new ExportResult(null, null, 0, [], "TooMany");

        var dir = ExportsDirectory();
        if (dir is null)
            return new ExportResult(null, null, 0, [], "OutputUnavailable");

        var raw = await db.Questions.AsNoTracking()
            .Where(q => ids.Contains(q.Id))
            .Select(q => new
            {
                q.Id, q.StableKey, q.OriginalCode, OriginalText = q.CorrectedText ?? q.OriginalText,
                OriginalOptionsJson = q.CorrectedOptionsJson ?? q.OriginalOptionsJson, q.OriginalCorrectIndex, q.CorrectedIndex,
                q.ReviewStatus, q.Location,
                Indicator = db.Indicators.Where(i => i.Id == q.IndicatorId)
                    .Select(i => new { i.Code, i.Name, i.MainDomain, i.SubDomain, F = db.Frameworks.Where(f => f.Id == i.FrameworkId).Select(f => f.Name + " " + f.Version).FirstOrDefault() })
                    .FirstOrDefault(),
                File = db.SourceFiles.Where(f => f.Id == q.SourceFileId).Select(f => f.RelativePath).FirstOrDefault(),
                Upload = db.Uploads.Where(u => u.Id == q.UploadId).Select(u => u.ClientPath ?? u.OriginalName).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var byId = raw.ToDictionary(r => r.Id);
        var imageRows = await (from qi in db.QuestionImages.AsNoTracking()
                               join b in db.ImageBlobs.AsNoTracking() on qi.Sha256 equals b.Sha256
                               where ids.Contains(qi.QuestionId)
                               orderby qi.QuestionId, qi.Order
                               select new { qi.QuestionId, qi.OptionIndex, b.Sha256, b.ContentType, b.Content })
            .ToListAsync(cancellationToken);
        var imagesById = imageRows.GroupBy(i => i.QuestionId)
            .ToDictionary(g => g.Key, g => g.Select(i => new RowImage(i.OptionIndex, i.Sha256, i.ContentType, i.Content)).ToList());
        var rejected = new List<ExportRejection>();
        var rows = new List<Row>();
        foreach (var id in ids) // ترتيب السلة كما اختاره المستخدم
        {
            if (!byId.TryGetValue(id, out var r)) { rejected.Add(new(id, "غير موجود")); continue; }
            var options = JsonSerializer.Deserialize<string[]>(r.OriginalOptionsJson) ?? [];
            var answer = r.CorrectedIndex ?? r.OriginalCorrectIndex;
            if (r.ReviewStatus != QuestionReviewStatus.Approved) { rejected.Add(new(id, "غير معتمد")); continue; }
            if (answer is null || answer < 0 || answer >= options.Length) { rejected.Add(new(id, "بلا إجابة صحيحة")); continue; }
            var images = imagesById.GetValueOrDefault(id) ?? [];
            if (images.Count > 0 && request.Format == ExportFormat.Gift)
            { rejected.Add(new(id, "سؤال مصوّر: صيغة GIFT لا تحمل الصور، صدّره بصيغة Moodle XML")); continue; }
            if (images.Any(m => !ExtractedImage.IsDisplayable(m.ContentType)))
            { rejected.Add(new(id, "صورة بصيغة لا يعرضها Moodle (مثل EMF أو WMF): تحتاج تحويلًا")); continue; }
            // الخيار أو النص بلا كتابة مقبول إذا كانت صورته هي محتواه.
            if (options.Length < 2
                || options.Where((o, i) => string.IsNullOrWhiteSpace(o) && !images.Any(m => m.OptionIndex == i)).Any()
                || (string.IsNullOrWhiteSpace(r.OriginalText) && !images.Any(m => m.OptionIndex is null)))
            { rejected.Add(new(id, "بنية ناقصة")); continue; }
            // فئة Moodle حسب المؤشر: الإطار وإصداره، ثم المجال، ثم الفرعي، ثم المؤشر (الرمز إن وُجد).
            var category = r.Indicator is { } ind
                ? string.Join("/", new[] { ind.F, ind.MainDomain, ind.SubDomain, ind.Code ?? ind.Name }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Replace('/', '∕')))
                : null;
            rows.Add(new Row(r.Id, r.StableKey, r.OriginalCode, r.OriginalText, options, answer, r.ReviewStatus,
                r.File ?? ("رفع يدوي/" + r.Upload), r.Location, category, r.Indicator?.Code) { Images = images });
        }
        if (rows.Count == 0)
            return new ExportResult(null, null, 0, rejected, "NothingToExport");

        var content = request.Format == ExportFormat.Gift
            ? BuildGift(rows)
            : BuildMoodleXml(rows, request.AllowShuffle, request.CategoryBySource);

        // الفحص الذاتي قبل الحفظ: ما يخرج يجب أن يُقرأ كما دخل.
        if (!Verify(request.Format, content, rows))
            return new ExportResult(null, null, 0, rejected, "VerificationFailed");

        var now = time.GetUtcNow().UtcDateTime;
        var record = new ExportRecord
        {
            Format = request.Format,
            RequestedBy = user,
            AtUtc = now,
            QuestionCount = rows.Count,
            RejectedCount = rejected.Count,
            FileName = "pending",
            SettingsJson = JsonSerializer.Serialize(new { request.Format, request.AllowShuffle, request.CategoryBySource, QuestionIds = rows.Select(r => r.Id) })
        };
        db.Exports.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        record.FileName = $"{now:yyyyMMdd-HHmmss}-{record.Id}.{(request.Format == ExportFormat.Gift ? "gift.txt" : "xml")}";
        await File.WriteAllTextAsync(Path.Combine(dir, record.FileName), content, new UTF8Encoding(false), cancellationToken);
        db.AuditLog.Add(new AuditEntry
        {
            AtUtc = now, User = user, Action = "Export", EntityType = nameof(ExportRecord), EntityId = record.Id,
            NewValue = $"{request.Format}:{rows.Count}"
        });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        return new ExportResult(record.Id, record.FileName, rows.Count, rejected, null);
    }

    public async Task<(string Path, string FileName, string ContentType)?> GetFileAsync(long id, CancellationToken cancellationToken)
    {
        var record = await db.Exports.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        var dir = ExportsDirectory();
        if (record is null || dir is null)
            return null;
        // اسم الملف من قاعدة البيانات (ننشئه نحن)، ومع ذلك نتحقق أنه داخل مجلد التصدير.
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, record.FileName));
        if (!SourcePaths.IsSameOrInside(full, dir) || !File.Exists(full))
            return null;
        return (full, record.FileName, record.Format == ExportFormat.Gift ? "text/plain; charset=utf-8" : "application/xml; charset=utf-8");
    }

    public async Task<IReadOnlyList<ExportRecord>> ListAsync(CancellationToken cancellationToken) =>
        await db.Exports.AsNoTracking().OrderByDescending(e => e.Id).Take(50).ToListAsync(cancellationToken);

    private string? ExportsDirectory()
    {
        var o = inventoryOptions.Value;
        if (string.IsNullOrWhiteSpace(o.OutputRoot) || !Path.IsPathFullyQualified(o.OutputRoot))
            return null;
        var output = SourcePaths.NormalizeRoot(o.OutputRoot);
        if (!string.IsNullOrWhiteSpace(o.SourceRoot) && Path.IsPathFullyQualified(o.SourceRoot)
            && SourcePaths.IsSameOrInside(output, SourcePaths.NormalizeRoot(o.SourceRoot)))
            return null;
        var dir = Path.Combine(output, "exports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------- GIFT ----------

    public static string GiftEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '~' or '=' or '#' or '{' or '}' or ':' or '\\')
                sb.Append('\\');
            sb.Append(c == '\n' ? ' ' : c == '\r' ? ' ' : c);
        }
        return sb.ToString();
    }

    private static string BuildGift(IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append("// UTF-8 · ترتيب الخيارات كما في الأصل\n\n");
        foreach (var r in rows)
        {
            // التعليق قبل السؤال للتتبع، بلا محارف سطر جديد قد تكسر الصيغة.
            sb.Append("// ").Append(OneLine(r.StableKey)).Append(" | مصدر: ").Append(OneLine(r.Source))
              .Append(" | ").Append(OneLine(r.Location)).Append('\n');
            sb.Append("::").Append(GiftEscape(r.Code ?? r.StableKey)).Append("::")
              .Append(GiftEscape(r.Text)).Append(" {\n");
            for (var i = 0; i < r.Options.Length; i++)
                sb.Append("  ").Append(i == r.Answer ? '=' : '~').Append(GiftEscape(r.Options[i])).Append('\n');
            sb.Append("}\n\n");
        }
        return sb.ToString();
    }

    // ---------- Moodle XML ----------

    private static string BuildMoodleXml(IReadOnlyList<Row> rows, bool allowShuffle, bool categoryBySource)
    {
        var quiz = new XElement("quiz");
        string? lastCategory = null;
        string CategoryOf(Row r) => r.IndicatorCategory ?? "بلا مؤشر/" + Folder(r.Source);
        IEnumerable<Row> ordered = categoryBySource ? rows.OrderBy(CategoryOf, StringComparer.Ordinal) : rows;
        foreach (var r in ordered)
        {
            if (categoryBySource)
            {
                var category = "$course$/top/بنك الأسئلة/" + CategoryOf(r);
                if (category != lastCategory)
                {
                    quiz.Add(new XElement("question", new XAttribute("type", "category"),
                        new XElement("category", new XElement("text", category))));
                    lastCategory = category;
                }
            }
            // "--" ممنوع داخل تعليقات XML.
            quiz.Add(new XComment($" {r.StableKey} | مصدر: {OneLine(r.Source)} | {OneLine(r.Location)} ".Replace("--", "- -")));
            quiz.Add(new XElement("question", new XAttribute("type", "multichoice"),
                new XElement("name", new XElement("text", r.Code ?? r.StableKey)),
                new XElement("questiontext", new XAttribute("format", "html"),
                    new XElement("text", new XCData(Html(r.Text, r.Images.Where(m => m.OptionIndex is null)))),
                    Files(r.Images.Where(m => m.OptionIndex is null))),
                new XElement("generalfeedback", new XAttribute("format", "html"), new XElement("text", "")),
                new XElement("defaultgrade", "1.0000000"),
                new XElement("penalty", "0.3333333"),
                new XElement("hidden", "0"),
                new XElement("idnumber", r.StableKey),
                new XElement("single", "true"),
                new XElement("shuffleanswers", allowShuffle ? "true" : "false"),
                new XElement("answernumbering", "none"),
                r.Options.Select((o, i) => new XElement("answer",
                    new XAttribute("fraction", i == r.Answer ? "100" : "0"), new XAttribute("format", "html"),
                    new XElement("text", new XCData(Html(o, r.Images.Where(m => m.OptionIndex == i)))),
                    Files(r.Images.Where(m => m.OptionIndex == i)),
                    new XElement("feedback", new XAttribute("format", "html"), new XElement("text", "")))),
                new XElement("tags",
                    new XElement("tag", new XElement("text", "source:" + Folder(r.Source))),
                    r.IndicatorCode is null ? null : new XElement("tag", new XElement("text", "indicator:" + r.IndicatorCode)))));
        }
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), quiz);
        return doc.Declaration + "\n" + doc.Root!.ToString() + "\n";
    }

    /// <summary>
    /// النص يُهرَّب كـ HTML ثم يوضع في CDATA. XCData يقسّم "]]>" تلقائيًا، فلا يكسر نص السؤال الملف.
    /// </summary>
    private static string Rtl(string text) =>
        "<p dir=\"rtl\">" + SecurityElement.Escape(text)!.Replace("\n", "<br>") + "</p>";

    /// <summary>النص ثم صوره بالترتيب. الصورة تشير إلى ملف مضمّن في السؤال نفسه (@@PLUGINFILE@@).</summary>
    private static string Html(string text, IEnumerable<RowImage> images)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(text))
            sb.Append(Rtl(text));
        foreach (var name in images.Select(m => m.FileName).Distinct())
            sb.Append("<p><img src=\"@@PLUGINFILE@@/").Append(name).Append("\" alt=\"\"></p>");
        return sb.ToString();
    }

    private static IEnumerable<XElement> Files(IEnumerable<RowImage> images) =>
        images.DistinctBy(m => m.FileName).Select(m => new XElement("file",
            new XAttribute("name", m.FileName), new XAttribute("path", "/"), new XAttribute("encoding", "base64"),
            Convert.ToBase64String(m.Content)));

    private static string Folder(string source)
    {
        var slash = source.LastIndexOf('/');
        return slash > 0 ? source[..slash] : "الجذر";
    }

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    // ---------- self-check ----------

    private static int DistinctImages(Row r) =>
        r.Images.Where(m => m.OptionIndex is null).DistinctBy(m => m.FileName).Count()
        + Enumerable.Range(0, r.Options.Length).Sum(i => r.Images.Where(m => m.OptionIndex == i).DistinctBy(m => m.FileName).Count());

    private static bool Verify(ExportFormat format, string content, IReadOnlyList<Row> rows)
    {
        if (format == ExportFormat.Gift)
        {
            var parsed = new GiftReader().Read(Encoding.UTF8.GetBytes(content)).Questions;
            if (parsed.Count != rows.Count)
                return false;
            for (var i = 0; i < rows.Count; i++)
            {
                var (p, r) = (parsed[i], rows[i]);
                if (p.CorrectIndex != r.Answer || p.Text != OneLine(r.Text).Trim()
                    || !p.Options.SequenceEqual(r.Options.Select(o => OneLine(o).Trim())))
                    return false;
            }
            return true;
        }

        try
        {
            var doc = XDocument.Parse(content);
            var questions = doc.Root!.Elements("question").Where(q => (string?)q.Attribute("type") == "multichoice").ToList();
            if (questions.Count != rows.Count
                || !questions.All(q => q.Elements("answer").Count(a => (string?)a.Attribute("fraction") == "100") == 1))
                return false;
            // كل صورة يجب أن تصل كما هي: الملف المضمّن يطابق بصمته، وكل ملف له إشارة في النص.
            var byKey = rows.ToDictionary(r => r.StableKey);
            foreach (var q in questions)
            {
                if (!byKey.TryGetValue((string?)q.Element("idnumber") ?? "", out var row))
                    return false;
                var parts = new[] { q.Element("questiontext")! }.Concat(q.Elements("answer")).ToList();
                var files = parts.SelectMany(p => p.Elements("file")).ToList();
                if (files.Count != DistinctImages(row))
                    return false;
                foreach (var part in parts)
                {
                    var html = (string?)part.Element("text") ?? "";
                    foreach (var f in part.Elements("file"))
                    {
                        var name = (string?)f.Attribute("name") ?? "";
                        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(f.Value)));
                        if (!name.StartsWith(sha[..16], StringComparison.OrdinalIgnoreCase) || !html.Contains("@@PLUGINFILE@@/" + name, StringComparison.Ordinal))
                            return false;
                    }
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException)
        {
            return false;
        }
    }
}
