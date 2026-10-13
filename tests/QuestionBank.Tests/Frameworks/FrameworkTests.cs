using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Export;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Frameworks;
using QuestionBank.Core.Review;
using QuestionBank.Tests.Inventory;
using MsOptions = Microsoft.Extensions.Options.Options;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace QuestionBank.Tests.Frameworks;

public sealed class FrameworkTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    public static byte[] Workbook(params string?[][] rows)
    {
        var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            var part = wb.AddNewPart<WorksheetPart>();
            var data = new X.SheetData();
            for (var r = 0; r < rows.Length; r++)
            {
                var row = new X.Row { RowIndex = (uint)(r + 1) };
                for (var c = 0; c < rows[r].Length; c++)
                    if (rows[r][c] is { } v)
                        row.Append(new X.Cell { CellReference = $"{(char)('A' + c)}{r + 1}", DataType = X.CellValues.InlineString, InlineString = new X.InlineString(new X.Text(v)) });
                data.Append(row);
            }
            part.Worksheet = new X.Worksheet(data);
            wb.Workbook = new X.Workbook(new X.Sheets(new X.Sheet { Name = "الإطار", SheetId = 1, Id = wb.GetIdOfPart(part) }));
        }
        return ms.ToArray();
    }

    // ملف إطار بأسلوب شائع: صفوف وصفية أولًا، ثم صف العناوين، والمجالات مكتوبة مرة واحدة لكل مجموعة (خلايا مدمجة).
    public static readonly byte[] SampleFramework = Workbook(
        ["إطار الكفايات الوظيفية"], ["الوظيفة: موظف إداري"], ["الإصدار: 2.1"], [],
        ["المجال الرئيسي", "المجال الفرعي", "رمز المؤشر", "المؤشر"],
        ["التخطيط والتنظيم", "إدارة الأولويات", "1.1.1", "يرتّب المهام حسب الأهمية والإلحاح"],
        [null, null, "1.1.2", "يوازن بين المهام المتعارضة في مواعيد التسليم"],
        [null, "إدارة الوقت", "1.2.1", "يضع جداول زمنية واقعية للمهام"],
        ["التواصل", "التواصل الكتابي", "2.1.1", "يصوغ المراسلات الرسمية بوضوح وإيجاز"],
        [null, "الإصغاء", "2.2.1", "يتحقق من فهمه لما يقوله الآخرون أثناء الاجتماع"]);

    [Fact]
    public void Importer_FindsHeaderAfterMetadata_CarriesMergedDomains_AndKeepsTextLiterally()
    {
        var r = FrameworkImporter.Parse(SampleFramework);

        Assert.Empty(r.Errors);
        Assert.Equal(5, r.Indicators.Count);
        Assert.Equal(("التخطيط والتنظيم", "إدارة الأولويات", "1.1.2"), (r.Indicators[1].MainDomain, r.Indicators[1].SubDomain, r.Indicators[1].Code));
        Assert.Equal(("التخطيط والتنظيم", "إدارة الوقت"), (r.Indicators[2].MainDomain, r.Indicators[2].SubDomain));
        Assert.Equal("يرتّب المهام حسب الأهمية والإلحاح", r.Indicators[0].Name); // الشدّة محفوظة
        Assert.Equal(6, r.Indicators[0].RowNo);
    }

    [Fact]
    public void Importer_ReportsDuplicateCodes_MissingHeaders_AndMissingCodeColumn()
    {
        var dup = FrameworkImporter.Parse(Workbook(["المجال", "رمز المؤشر", "المؤشر"], ["أ", "1", "س"], ["أ", "1", "ص"]));
        Assert.Contains(dup.Errors, e => e.Contains("مكررة"));

        var none = FrameworkImporter.Parse(Workbook(["عمود", "آخر"], ["x", "y"]));
        Assert.Single(none.Errors);
        Assert.Contains("عمود", none.Errors[0]);

        var noCode = FrameworkImporter.Parse(Workbook(["Domain", "Indicator"], ["Planning", "Prioritises tasks"]));
        Assert.Empty(noCode.Errors);
        Assert.Contains(noCode.Warnings, w => w.Contains("رمز"));
    }

    private async Task<(long FrameworkId, List<long> Questions)> SeedAsync()
    {
        const string txt = """
            1. عند تعارض مهمتين في موعد التسليم، ما أول إجراء لموازنة المهام المتعارضة؟
            أ) التأجيل
            *ب) تقييم الأثر ومناقشة الأولوية

            2. أي سلوك يدل على التحقق من فهم ما يقوله الآخرون في الاجتماع؟
            أ) المقاطعة
            *ب) إعادة صياغة ما قيل

            3. ما الهدف من صياغة المراسلات الرسمية بإيجاز؟
            *أ) الوضوح وتوفير الوقت
            ب) إظهار البلاغة
            """;
        _kit.WriteFile("إداري/أسئلة.txt", txt);
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        var file = await db.SourceFiles.SingleAsync();
        await new ExtractionService(db, ReaderRegistry.Default(), _kit.Time).ExtractAsync(Encoding.UTF8.GetBytes(txt), "txt", file.Id, null, CancellationToken.None);
        var import = await Service(db).ImportAsync(SampleFramework, "إطار.xlsx", "إطار وظيفة إدارية", "2.1", "CORP\\m", CancellationToken.None);
        return (import.Framework!.Id, await db.Questions.OrderBy(q => q.OriginalOrder).Select(q => q.Id).ToListAsync());
    }

    private FrameworkService Service(QuestionBank.Core.Data.QuestionBankDbContext db) => new(db, new LexicalSuggester(), _kit.Time);

    [Fact]
    public async Task Import_StoresIndicators_AndRejectsSameNameAndVersion()
    {
        var (fid, _) = await SeedAsync();
        await using var db = _kit.CreateDb();
        Assert.Equal(5, await db.Indicators.CountAsync(i => i.FrameworkId == fid));
        var again = await Service(db).ImportAsync(SampleFramework, "إطار.xlsx", "إطار وظيفة إدارية", "2.1", "u", CancellationToken.None);
        Assert.Null(again.Framework);
        Assert.Contains(again.Errors, e => e.Contains("إصدار"));
        Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "ImportFramework");
    }

    [Fact]
    public async Task LexicalSuggestions_RankTheRelatedIndicatorFirst()
    {
        var (fid, qs) = await SeedAsync();
        await using var db = _kit.CreateDb();
        var items = await Service(db).UnclassifiedAsync(fid, 50, CancellationToken.None);

        string Top(long id) => items.Single(i => i.Id == id).Suggestions[0].Code!;
        Assert.Equal("1.1.2", Top(qs[0]));
        Assert.Equal("2.2.1", Top(qs[1]));
        Assert.Equal("2.1.1", Top(qs[2]));
        Assert.All(items, i => Assert.True(i.Suggestions.Count <= 3));
        Assert.Contains("كلمات مشتركة", items[0].Suggestions[0].Rationale);
    }

    [Fact]
    public void LexicalSuggester_ShowsNothingForUnrelatedQuestions_AndStemsArticles()
    {
        var indicators = FrameworkImporter.Parse(SampleFramework).Indicators
            .Select((p, i) => new CompetencyIndicator { Id = i + 1, MainDomain = p.MainDomain, SubDomain = p.SubDomain, Code = p.Code, Name = p.Name, SourceRow = "" }).ToList();
        Assert.Empty(new LexicalSuggester().Suggest("كم يومًا في الأسبوع؟", ["خمسة", "سبعة"], indicators, 3));
        Assert.Equal("اولو", LexicalSuggester.LightStem("الاولويه"));
        Assert.Equal("اردن", LexicalSuggester.LightStem("الاردن"));
    }

    [Fact]
    public async Task Assign_OnlyToExistingIndicators_IsAudited_AndUpdatesTreeCounts()
    {
        var (fid, qs) = await SeedAsync();
        await using var db = _kit.CreateDb();
        var svc = Service(db);
        var indicator = await db.Indicators.SingleAsync(i => i.Code == "1.1.2");

        Assert.Equal(ClassifyOutcome.IndicatorNotFound, await svc.AssignAsync(qs[0], 99999, "manual", null, "u", CancellationToken.None));
        Assert.Equal(ClassifyOutcome.Ok, await svc.AssignAsync(qs[0], indicator.Id, "lexical", 0.42, "CORP\\r", CancellationToken.None));
        Assert.Equal(ClassifyOutcome.Ok, await svc.AssignAsync(qs[1], indicator.Id, "anything-else", null, "CORP\\r", CancellationToken.None));

        var q0 = await db.Questions.AsNoTracking().SingleAsync(q => q.Id == qs[0]);
        Assert.Equal((MatchStatus.Matched, "lexical", fid, "CORP\\r"), (q0.MatchStatus, q0.MatchMethod, q0.FrameworkId!.Value, q0.MatchedBy));
        Assert.Equal("manual", (await db.Questions.AsNoTracking().SingleAsync(q => q.Id == qs[1])).MatchMethod);

        var tree = await svc.TreeAsync(fid, CancellationToken.None);
        Assert.Equal(2, tree!.Domains.SelectMany(d => d.Subs).SelectMany(s => s.Indicators).Single(i => i.Code == "1.1.2").Questions);
        Assert.Equal(4, tree.IndicatorsWithoutQuestions);
        Assert.Equal(["التخطيط والتنظيم", "التواصل"], tree.Domains.Select(d => d.Name).ToArray());
        Assert.Single(await svc.UnclassifiedAsync(fid, 50, CancellationToken.None));

        Assert.Equal(ClassifyOutcome.Ok, await svc.AssignAsync(qs[0], null, "manual", null, "u", CancellationToken.None));
        Assert.Equal(MatchStatus.None, (await db.Questions.AsNoTracking().SingleAsync(q => q.Id == qs[0])).MatchStatus);
        Assert.Equal(["Classify", "Classify", "Unclassify"],
            (await db.AuditLog.Where(a => a.EntityType == "Question").OrderBy(a => a.Id).Select(a => a.Action).ToListAsync()).ToArray());
    }

    [Fact]
    public async Task MoodleXml_UsesIndicatorCategoryAndTag_WhenClassified()
    {
        var (_, qs) = await SeedAsync();
        await using (var db = _kit.CreateDb())
        {
            var indicator = await db.Indicators.SingleAsync(i => i.Code == "1.1.2");
            await Service(db).AssignAsync(qs[0], indicator.Id, "manual", null, "u", CancellationToken.None);
            var review = new ReviewService(db, _kit.Time);
            await review.ApproveAsync(qs[0], "u", CancellationToken.None);
            await review.ApproveAsync(qs[2], "u", CancellationToken.None);
        }
        _kit.Options.SourceRoot = _kit.SourceRoot;
        _kit.Options.OutputRoot = _kit.OutputRoot;
        await using var db2 = _kit.CreateDb();
        var result = await new ExportService(db2, MsOptions.Create(_kit.Options), _kit.Time)
            .ExportAsync(new ExportRequest(ExportFormat.MoodleXml, [qs[0], qs[2]], false, true), "u", CancellationToken.None);

        var doc = XDocument.Load(Path.Combine(_kit.OutputRoot, "exports", result.FileName!));
        var categories = doc.Root!.Elements("question").Where(q => (string?)q.Attribute("type") == "category")
            .Select(q => q.Element("category")!.Element("text")!.Value).ToList();
        Assert.Contains("$course$/top/بنك الأسئلة/إطار وظيفة إدارية 2.1/التخطيط والتنظيم/إدارة الأولويات/1.1.2", categories);
        Assert.Contains("$course$/top/بنك الأسئلة/بلا مؤشر/إداري", categories);
        Assert.Contains(doc.Descendants("tag"), t => t.Value == "indicator:1.1.2");
    }
}
