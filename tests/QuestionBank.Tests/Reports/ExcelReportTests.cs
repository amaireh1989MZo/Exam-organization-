using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Reports;
using QuestionBank.Tests.Inventory;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace QuestionBank.Tests.Reports;

public sealed class ExcelReportTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private ExcelReportService Service(QuestionBank.Core.Data.QuestionBankDbContext db)
    {
        _kit.Options.SourceRoot = _kit.SourceRoot;
        _kit.Options.OutputRoot = _kit.OutputRoot;
        return new ExcelReportService(db, MsOptions.Create(_kit.Options), _kit.Time);
    }

    private static Dictionary<string, List<List<string>>> ReadWorkbook(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        return wb.Workbook!.Sheets!.Elements<Sheet>().ToDictionary(
            s => s.Name!.Value!,
            s => ((WorksheetPart)wb.GetPartById(s.Id!)).Worksheet!.Descendants<Row>()
                .Select(r => r.Elements<Cell>().Select(c => c.DataType?.Value == CellValues.InlineString ? c.InnerText : c.CellValue?.Text ?? "").ToList())
                .ToList());
    }

    [Fact]
    public async Task Report_HasAllSheets_RtlRows_AndFormulaTextStaysText()
    {
        const string txt = "1. =SUM(A1:A9) سؤال خطر؟\nأ) س\n*ب) ص\n\n2. سؤال بلا إجابة؟\nأ) ل\nب) م\n";
        _kit.WriteFile("مجلد/أ.txt", txt);
        _kit.WriteFile("مجلد/ب.txt", txt);
        _kit.WriteFile("مجلد/ج.docx", "x");
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        var extraction = new ExtractionService(db, ReaderRegistry.Default(), _kit.Time);
        foreach (var f in await db.SourceFiles.Where(f => f.Extension == "txt").ToListAsync())
            await extraction.ExtractAsync(Encoding.UTF8.GetBytes(txt), "txt", f.Id, null, CancellationToken.None);

        var result = await Service(db).GenerateAsync("CORP\\m", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Matches(ExcelReportService.FileNamePattern, result!.FileName);
        Assert.Equal((3, 4, 4), (result.Files, result.Questions, result.DuplicateRows));
        var path = Service(db).PathOf(result.FileName)!;
        var sheets = ReadWorkbook(path);
        Assert.Equal(["ملخص", "الملفات", "الأسئلة", "التكرار الحرفي", "الأخطاء"], sheets.Keys.ToArray());
        var q = sheets["الأسئلة"];
        Assert.Equal(5, q.Count); // ترويسة + 4
        Assert.Equal("=SUM(A1:A9) سؤال خطر؟", q[1][6]);
        Assert.Equal("ب", q[1][10]);
        Assert.Equal("ص", q[1][11]);
        using (var doc = SpreadsheetDocument.Open(path, false))
        {
            Assert.Empty(doc.WorkbookPart!.WorksheetParts.SelectMany(p => p.Worksheet!.Descendants<CellFormula>()));
            Assert.All(doc.WorkbookPart.WorksheetParts, p => Assert.True(p.Worksheet!.Descendants<SheetView>().Single().RightToLeft!.Value));
        }
        Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "ExcelReport");
    }

    [Theory]
    [InlineData("../secret.xlsx")]
    [InlineData("report-20261013-000000-abcdef.xlsx/../../x")]
    [InlineData("other.xlsx")]
    public async Task PathOf_RejectsAnythingButGeneratedNames(string name)
    {
        await using var db = _kit.CreateDb();
        Assert.Null(Service(db).PathOf(name));
    }

    [Fact]
    public async Task ControlCharactersAndHugeTexts_DoNotBreakTheFile()
    {
        var huge = new string('س', 40_000);
        var txt = $"1. نص\u0001فيه محرف تحكم {huge}؟\nأ) س\n*ب) ص\n";
        _kit.WriteFile("أ.txt", txt);
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        var f = await db.SourceFiles.SingleAsync();
        await new ExtractionService(db, ReaderRegistry.Default(), _kit.Time).ExtractAsync(Encoding.UTF8.GetBytes(txt), "txt", f.Id, null, CancellationToken.None);

        var result = await Service(db).GenerateAsync("u", CancellationToken.None);

        var text = ReadWorkbook(Service(db).PathOf(result!.FileName)!)["الأسئلة"][1][6];
        Assert.DoesNotContain('\u0001', text);
        Assert.True(text.Length <= 32_767);
        Assert.EndsWith("(مقصوص)", text);
    }
}
