using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestionBank.Core.Data;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Inventory;
using QuestionBank.Core.Review;

namespace QuestionBank.Core.Reports;

public sealed record ReportResult(string FileName, int Files, int Questions, int DuplicateRows, int Errors);

/// <summary>
/// تقرير Excel شامل: ملخص، والملفات، والأسئلة، والتكرار الحرفي، والأخطاء.
/// يُكتب بطريقة متدفقة (OpenXmlWriter) صفًا بعد صف من قاعدة البيانات، فيتحمل مئات آلاف الأسئلة دون استهلاك ذاكرة كبير.
/// كل النصوص تُكتب كنص (InlineString) وليس كصيغة، فلا تُنفَّذ خلية تبدأ بـ "=".
/// </summary>
public sealed class ExcelReportService(QuestionBankDbContext db, IOptions<InventoryOptions> inventoryOptions, TimeProvider time)
{
    public const string FileNamePattern = @"^report-\d{8}-\d{6}-[0-9a-f]{6}\.xlsx$";
    private const int MaxCellLength = 32_767;
    private static readonly string[] Letters = ["أ", "ب", "ج", "د", "هـ", "و", "ز", "ح"];

    public async Task<ReportResult?> GenerateAsync(string user, CancellationToken cancellationToken)
    {
        var dir = ReportsDirectory();
        if (dir is null)
            return null;

        var now = time.GetUtcNow().UtcDateTime;
        var fileName = $"report-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.xlsx";
        var path = Path.Combine(dir, fileName);
        int files = 0, questions = 0, dups = 0, errors = 0;

        try
        {
            using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
            {
                var wb = doc.AddWorkbookPart();
                wb.AddNewPart<WorkbookStylesPart>().Stylesheet = Styles();
                var sheets = new List<(string Name, string Id)>();

                sheets.Add(("ملخص", await WriteSheetAsync(wb, w => WriteSummaryAsync(w, now, user, cancellationToken))));
                sheets.Add(("الملفات", await WriteSheetAsync(wb, async w => files = await WriteFilesAsync(w, cancellationToken))));
                sheets.Add(("الأسئلة", await WriteSheetAsync(wb, async w => questions = await WriteQuestionsAsync(w, cancellationToken))));
                sheets.Add(("التكرار الحرفي", await WriteSheetAsync(wb, async w => dups = await WriteDuplicatesAsync(w, cancellationToken))));
                sheets.Add(("الأخطاء", await WriteSheetAsync(wb, async w => errors = await WriteErrorsAsync(w, cancellationToken))));
                if (await db.Frameworks.AnyAsync(cancellationToken))
                    sheets.Add(("التغطية", await WriteSheetAsync(wb, w => WriteCoverageAsync(w, cancellationToken))));

                wb.Workbook = new Workbook(new Sheets(sheets.Select((s, i) => new Sheet { Name = s.Name, SheetId = (uint)(i + 1), Id = s.Id })));
                wb.Workbook.Save();
            }
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }

        db.AuditLog.Add(new AuditEntry
        {
            AtUtc = now, User = user, Action = "ExcelReport", EntityType = "Report", EntityId = 0,
            NewValue = $"{fileName}:{questions}"
        });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return new ReportResult(fileName, files, questions, dups, errors);
    }

    /// <summary>مسار تقرير موجود، بعد التحقق من صيغة الاسم وأنه داخل مجلد التقارير.</summary>
    public string? PathOf(string fileName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, FileNamePattern))
            return null;
        var dir = ReportsDirectory();
        if (dir is null) return null;
        var full = Path.GetFullPath(Path.Combine(dir, fileName));
        return SourcePaths.IsSameOrInside(full, dir) && File.Exists(full) ? full : null;
    }

    private async Task WriteSummaryAsync(OpenXmlWriter w, DateTime now, string user, CancellationToken ct)
    {
        Header(w, "البند", "القيمة");
        var byStatus = await db.SourceFiles.AsNoTracking().Where(f => !f.IsMissing).GroupBy(f => f.Status)
            .Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct);
        var byReview = await db.Questions.AsNoTracking().GroupBy(q => q.ReviewStatus)
            .Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct);
        Row(w, "تاريخ التقرير (UTC)", now.ToString("yyyy-MM-dd HH:mm"));
        Row(w, "أنشأه", user);
        Row(w, "ملفات مسجلة", byStatus.Sum(x => x.C));
        foreach (var s in byStatus.OrderBy(x => x.Key)) Row(w, "ملفات: " + s.Key, s.C);
        Row(w, "ملفات مفقودة من المصدر", await db.SourceFiles.CountAsync(f => f.IsMissing, ct));
        Row(w, "ملفات مرفوعة يدويًا", await db.Uploads.CountAsync(ct));
        Row(w, "أسئلة مستخرجة", byReview.Sum(x => x.C));
        foreach (var r in byReview.OrderBy(x => x.Key)) Row(w, "أسئلة: " + r.Key, r.C);
        Row(w, "عمليات تصدير", await db.Exports.CountAsync(ct));
    }

    private async Task<int> WriteFilesAsync(OpenXmlWriter w, CancellationToken ct)
    {
        Header(w, "المسار النسبي", "الامتداد", "الحجم (بايت)", "آخر تعديل (UTC)", "الحالة", "رمز الخطأ", "مفقود", "بصمة المحتوى", "عدد الأسئلة");
        var counts = await db.Questions.AsNoTracking().Where(q => q.SourceFileId != null && q.ReviewStatus != QuestionReviewStatus.Superseded)
            .GroupBy(q => q.SourceFileId!.Value).Select(g => new { g.Key, C = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var n = 0;
        await foreach (var f in db.SourceFiles.AsNoTracking().OrderBy(f => f.RelativePath).AsAsyncEnumerable().WithCancellation(ct))
        {
            Row(w, f.RelativePath, f.Extension ?? "", f.SizeBytes, f.LastWriteUtc.ToString("yyyy-MM-dd HH:mm"),
                f.Status.ToString(), f.LastErrorCode ?? "", f.IsMissing ? "نعم" : "", f.ContentHash ?? "", counts.GetValueOrDefault(f.Id));
            n++;
        }
        return n;
    }

    private async Task<int> WriteQuestionsAsync(OpenXmlWriter w, CancellationToken ct)
    {
        Header(w, "المعرّف", "المعرّف الثابت", "المصدر", "الموقع", "الترتيب", "الرمز", "نص السؤال", "الخيارات",
            "إجابة الملف", "الإجابة المصحّحة", "الإجابة الفعّالة", "نص الإجابة الفعّالة", "طريقة الكشف", "الثقة",
            "حالة المراجعة", "ملاحظات", "راجعه", "القارئ", "النص المصحّح", "الخيارات المصحّحة");
        var files = await db.SourceFiles.AsNoTracking().Select(f => new { f.Id, f.RelativePath }).ToDictionaryAsync(f => f.Id, f => f.RelativePath, ct);
        var uploads = await db.Uploads.AsNoTracking().Select(u => new { u.Id, Name = u.ClientPath ?? u.OriginalName }).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var n = 0;
        await foreach (var q in db.Questions.AsNoTracking().OrderBy(q => q.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            var options = JsonSerializer.Deserialize<string[]>(q.OriginalOptionsJson) ?? [];
            var effective = q.CorrectedIndex ?? q.OriginalCorrectIndex;
            var issues = q.IssuesJson is null ? "" : string.Join(" · ", JsonSerializer.Deserialize<string[]>(q.IssuesJson) ?? []);
            var source = q.SourceFileId is { } fid ? files.GetValueOrDefault(fid, "") : "رفع يدوي/" + uploads.GetValueOrDefault(q.UploadId ?? 0, "");
            Row(w, q.Id, q.StableKey, source, q.Location, q.OriginalOrder, q.OriginalCode ?? "", q.OriginalText,
                string.Join(" | ", options.Select((o, i) => $"{Letter(i)}) {o}")),
                Letter(q.OriginalCorrectIndex), Letter(q.CorrectedIndex), Letter(effective),
                effective is { } e && e < options.Length ? options[e] : "",
                q.AnswerDetector, q.Confidence.ToString(), q.ReviewStatus.ToString(), issues, q.ReviewedBy ?? "", $"{q.ReaderName} {q.ReaderVersion}",
                q.CorrectedText ?? "",
                q.CorrectedOptionsJson is null ? "" : string.Join(" | ", (JsonSerializer.Deserialize<string[]>(q.CorrectedOptionsJson) ?? []).Select((o, i) => $"{Letter(i)}) {o}")));
            n++;
        }
        return n;
    }

    private async Task<int> WriteDuplicatesAsync(OpenXmlWriter w, CancellationToken ct)
    {
        Header(w, "رقم المجموعة", "عدد النسخ", "معرّف السؤال", "حالة المراجعة", "المصدر", "نص السؤال");
        var groups = await db.Questions.AsNoTracking()
            .Where(q => q.ReviewStatus != QuestionReviewStatus.Superseded && q.FullHash != null)
            .GroupBy(q => q.FullHash!).Where(g => g.Count() > 1)
            .Select(g => new { g.Key, C = g.Count() }).OrderByDescending(g => g.C).ToListAsync(ct);
        var files = await db.SourceFiles.AsNoTracking().Select(f => new { f.Id, f.RelativePath }).ToDictionaryAsync(f => f.Id, f => f.RelativePath, ct);
        var n = 0;
        var groupNo = 0;
        foreach (var chunk in groups.Chunk(200))
        {
            var keys = chunk.Select(g => g.Key).ToList();
            var members = await db.Questions.AsNoTracking()
                .Where(q => keys.Contains(q.FullHash!) && q.ReviewStatus != QuestionReviewStatus.Superseded)
                .Select(q => new { q.Id, q.FullHash, q.ReviewStatus, q.SourceFileId, q.UploadId, q.OriginalText })
                .ToListAsync(ct);
            foreach (var g in chunk)
            {
                groupNo++;
                foreach (var m in members.Where(m => m.FullHash == g.Key).OrderBy(m => m.Id))
                {
                    Row(w, groupNo, g.C, m.Id, m.ReviewStatus.ToString(),
                        m.SourceFileId is { } fid ? files.GetValueOrDefault(fid, "") : $"رفع يدوي #{m.UploadId}", m.OriginalText);
                    n++;
                }
            }
        }
        return n;
    }

    private async Task<int> WriteErrorsAsync(OpenXmlWriter w, CancellationToken ct)
    {
        Header(w, "النوع", "المسار أو الاسم", "رمز الخطأ", "وقت الخطأ (UTC)", "المحاولات");
        var n = 0;
        await foreach (var f in db.SourceFiles.AsNoTracking().Where(f => f.Status == FileStatus.Failed).OrderBy(f => f.RelativePath).AsAsyncEnumerable().WithCancellation(ct))
        {
            Row(w, "ملف مصدر", f.RelativePath, f.LastErrorCode ?? "", f.LastErrorAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "", f.Attempts);
            n++;
        }
        await foreach (var u in db.Uploads.AsNoTracking().Where(u => u.Status == FileStatus.Failed).OrderBy(u => u.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            Row(w, "ملف مرفوع", u.ClientPath ?? u.OriginalName, u.LastErrorCode ?? "", u.ProcessedAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "", 1);
            n++;
        }
        var failedDirs = await db.ScanDirectories.AsNoTracking().Where(d => d.Status == ScanDirectoryStatus.Failed).ToListAsync(ct);
        foreach (var d in failedDirs)
        {
            Row(w, "مجلد تعذّر جرده", d.RelativePath.Length == 0 ? "(الجذر)" : d.RelativePath, d.LastErrorCode ?? "", "", d.Attempts);
            n++;
        }
        return n;
    }

    /// <summary>كل مؤشر في كل إطار وعدد أسئلته (المعتمدة وكلها). المؤشر بلا أسئلة يظهر بصفر.</summary>
    private async Task WriteCoverageAsync(OpenXmlWriter w, CancellationToken ct)
    {
        Header(w, "الإطار", "الإصدار", "المجال الرئيسي", "المجال الفرعي", "رمز المؤشر", "المؤشر", "كل الأسئلة", "المعتمدة");
        var counts = await db.Questions.AsNoTracking()
            .Where(q => q.IndicatorId != null && q.ReviewStatus != QuestionReviewStatus.Superseded)
            .GroupBy(q => q.IndicatorId!.Value)
            .Select(g => new { g.Key, All = g.Count(), Approved = g.Count(q => q.ReviewStatus == QuestionReviewStatus.Approved) })
            .ToDictionaryAsync(x => x.Key, ct);
        var frameworks = await db.Frameworks.AsNoTracking().ToDictionaryAsync(f => f.Id, ct);
        await foreach (var i in db.Indicators.AsNoTracking().OrderBy(i => i.FrameworkId).ThenBy(i => i.Order).AsAsyncEnumerable().WithCancellation(ct))
        {
            var f = frameworks[i.FrameworkId];
            var c = counts.GetValueOrDefault(i.Id);
            Row(w, f.Name, f.Version, i.MainDomain, i.SubDomain ?? "", i.Code ?? "", i.Name, c?.All ?? 0, c?.Approved ?? 0);
        }
    }

    // ---------- OpenXML helpers ----------

    private static async Task<string> WriteSheetAsync(WorkbookPart wb, Func<OpenXmlWriter, Task> body)
    {
        var part = wb.AddNewPart<WorksheetPart>();
        using (var w = OpenXmlWriter.Create(part))
        {
            w.WriteStartElement(new Worksheet());
            // الورقة من اليمين لليسار، والصف الأول مثبت.
            w.WriteElement(new SheetViews(new SheetView(
                new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen })
            { RightToLeft = true, WorkbookViewId = 0 }));
            w.WriteStartElement(new SheetData());
            await body(w);
            w.WriteEndElement();
            w.WriteEndElement();
        }
        return wb.GetIdOfPart(part);
    }

    private static void Header(OpenXmlWriter w, params string[] titles) => WriteRow(w, titles.Cast<object>(), 1);

    private static void Row(OpenXmlWriter w, params object[] values) => WriteRow(w, values, 0);

    private static void WriteRow(OpenXmlWriter w, IEnumerable<object> values, uint style)
    {
        w.WriteStartElement(new Row());
        foreach (var v in values)
        {
            if (v is int or long)
            {
                w.WriteElement(new Cell { DataType = CellValues.Number, CellValue = new CellValue((decimal)Convert.ToInt64(v)), StyleIndex = style });
                continue;
            }
            w.WriteElement(new Cell(new InlineString(new Text(Clean(v?.ToString() ?? "")) { Space = SpaceProcessingModeValues.Preserve }))
            {
                DataType = CellValues.InlineString,
                StyleIndex = style
            });
        }
        w.WriteEndElement();
    }

    /// <summary>يحذف المحارف غير المسموحة في XML ويقص النص إلى حد Excel للخلية.</summary>
    private static string Clean(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, MaxCellLength));
        foreach (var c in s)
        {
            if (c is '\t' or '\n' or '\r' || (c >= ' ' && c != '￾' && c != '￿'))
                sb.Append(c);
            if (sb.Length >= MaxCellLength - 20)
            {
                sb.Append(" … (مقصوص)");
                break;
            }
        }
        return sb.ToString();
    }

    private static string Letter(int? i) => i is null ? "" : i < Letters.Length ? Letters[i.Value] : (i.Value + 1).ToString();

    private static Stylesheet Styles() => new(
        new Fonts(new Font(), new Font(new Bold())),
        new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
        new Borders(new Border()),
        new CellFormats(new CellFormat(), new CellFormat { FontId = 1, ApplyFont = true }));

    private string? ReportsDirectory()
    {
        var o = inventoryOptions.Value;
        if (string.IsNullOrWhiteSpace(o.OutputRoot) || !Path.IsPathFullyQualified(o.OutputRoot))
            return null;
        var output = SourcePaths.NormalizeRoot(o.OutputRoot);
        if (!string.IsNullOrWhiteSpace(o.SourceRoot) && Path.IsPathFullyQualified(o.SourceRoot)
            && SourcePaths.IsSameOrInside(output, SourcePaths.NormalizeRoot(o.SourceRoot)))
            return null;
        var dir = Path.Combine(output, "reports");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
