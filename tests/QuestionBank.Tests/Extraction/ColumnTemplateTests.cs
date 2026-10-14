using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Inventory;
using QuestionBank.Tests.Inventory;
using MsOptions = Microsoft.Extensions.Options.Options;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace QuestionBank.Tests.Extraction;

public sealed class ColumnTemplateTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    // عناوين لا يعرفها القارئ تلقائيًا.
    private static readonly string[] Headers = ["م", "الفقرة الاختبارية", "بديل أول", "بديل ثانٍ", "بديل ثالث", "المفتاح"];

    private static byte[] Workbook(params string[][] rows)
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
                    row.Append(new X.Cell { CellReference = $"{(char)('A' + c)}{r + 1}", DataType = X.CellValues.InlineString,
                        InlineString = new X.InlineString(new X.Text(rows[r][c])) });
                data.Append(row);
            }
            part.Worksheet = new X.Worksheet(data);
            wb.Workbook = new X.Workbook(new X.Sheets(new X.Sheet { Name = "بنك", SheetId = 1, Id = wb.GetIdOfPart(part) }));
        }
        return ms.ToArray();
    }

    private static readonly byte[] Sample = Workbook(Headers,
        ["1", "ما عاصمة الأردن؟", "إربد", "عمّان", "الزرقاء", "ب"],
        ["2", "أي مصدر متجدد؟", "الفحم", "الرياح", "النفط", "ب"]);

    private TemplateRequest Request(string? answer = "المفتاح") =>
        new("بنك المحاسبة", Headers, "الفقرة الاختبارية", ["بديل أول", "بديل ثانٍ", "بديل ثالث"], answer, "م");

    private UploadService Uploads(QuestionBank.Core.Data.QuestionBankDbContext db)
    {
        _kit.Options.SourceRoot = _kit.SourceRoot;
        _kit.Options.OutputRoot = _kit.OutputRoot;
        return new UploadService(db, MsOptions.Create(_kit.Options), MsOptions.Create(new UploadOptions()),
            new ExtractionService(db, ReaderRegistry.Default(), _kit.Time), _kit.Time);
    }

    [Fact]
    public void UnknownHeaders_WithoutTemplate_NeedMapping_WithTemplate_AreRead()
    {
        var plain = new XlsxReader().Read(Sample);
        Assert.Empty(plain.Questions);
        Assert.Contains(plain.Warnings, w => w.Contains(XlsxReader.NeedsMappingMarker));

        var map = new ColumnTemplateMap(XlsxReader.HeaderKey(Headers), "الفقرة الاختبارية", ["بديل أول", "بديل ثانٍ", "بديل ثالث"], "المفتاح", "م");
        var r = new XlsxReader([map]).Read(Sample);
        Assert.Equal(2, r.Questions.Count);
        Assert.Equal(("1", "ما عاصمة الأردن؟", 1), (r.Questions[0].Code, r.Questions[0].Text, r.Questions[0].CorrectIndex));
    }

    [Fact]
    public void Template_MatchesOnlyTheSameHeaderRow_IgnoringDiacriticsAndHamza()
    {
        var map = new ColumnTemplateMap(XlsxReader.HeaderKey(Headers), "الفقرة الاختبارية", ["بديل أول", "بديل ثانٍ"], null, null);
        var same = Workbook(["م", "الفقرةُ الاختبارية", "بديل اول", "بديل ثانٍ", "بديل ثالث", "المفتاح"], ["1", "س؟", "أ", "ب", "ج", "أ"]);
        var other = Workbook(["عمود آخر", "الفقرة الاختبارية", "بديل أول", "بديل ثانٍ"], ["1", "س؟", "أ", "ب"]);

        Assert.Single(new XlsxReader([map]).Read(same).Questions);
        Assert.Empty(new XlsxReader([map]).Read(other).Questions);
    }

    [Theory]
    [InlineData("", "عمود نص السؤال")]
    [InlineData("missing", "عمود نص السؤال")]
    public async Task InvalidTemplates_AreRejected(string stem, string expectedError)
    {
        await using var db = _kit.CreateDb();
        var svc = new ColumnTemplateService(db, _kit.Time);
        var (t, error) = await svc.SaveAsync(Request() with { Stem = stem }, "u", CancellationToken.None);
        Assert.Null(t);
        Assert.Contains(expectedError, error);

        var (t2, e2) = await svc.SaveAsync(Request() with { Options = ["بديل أول"] }, "u", CancellationToken.None);
        Assert.Null(t2);
        Assert.Contains("عمودين", e2);

        var (t3, e3) = await svc.SaveAsync(Request() with { Answer = "بديل أول" }, "u", CancellationToken.None);
        Assert.Null(t3);
        Assert.Contains("العمود نفسه", e3);
    }

    [Fact]
    public async Task UploadNeedingMapping_IsMarked_ThenTemplateAndReextractExtractsQuestions()
    {
        await using var db = _kit.CreateDb();
        var uploads = Uploads(db);
        var first = await uploads.SaveAsync(new MemoryStream(Sample), "بنك.xlsx", null, "u", CancellationToken.None);
        Assert.Equal("NeedsReview", first.Upload!.Status);
        Assert.Equal(UploadService.NeedsColumnMapping, first.Upload.LastErrorCode);

        var (template, _) = await new ColumnTemplateService(db, _kit.Time).SaveAsync(Request(), "CORP\\m", CancellationToken.None);
        Assert.NotNull(template);
        var view = await uploads.ReextractAsync(first.Upload.Id, CancellationToken.None);

        Assert.Equal(("Completed", 2), (view!.Status, view.QuestionCount));
        Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "SaveColumnTemplate");
    }

    [Fact]
    public async Task SavingTheSameHeaderRowAgain_UpdatesTheTemplate()
    {
        await using var db = _kit.CreateDb();
        var svc = new ColumnTemplateService(db, _kit.Time);
        await svc.SaveAsync(Request(answer: null), "u", CancellationToken.None);
        await svc.SaveAsync(Request() with { Name = "اسم جديد" }, "u", CancellationToken.None);

        var t = Assert.Single(await svc.ListAsync(CancellationToken.None));
        Assert.Equal(("اسم جديد", "المفتاح"), (t.Name, t.Answer));
    }

    [Fact]
    public async Task SourceFilesWaitingForMapping_AreRequeued_OnlyTheirFolders()
    {
        File.WriteAllBytes(_kit.FullPath("محاسبة/2024/بنك.xlsx").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)), Sample);
        _kit.WriteFile("هندسة/أسئلة.txt", "1. س؟\nأ) ا\n*ب) ب");
        await _kit.ScanAsync();
        await using (var db = _kit.CreateDb())
            await new FolderService(db, _kit.Time).EnqueueAsync("محاسبة", "u", CancellationToken.None);
        await using (var db = _kit.CreateDb())
        {
            var processor = new QueueProcessor(db, new PhysicalSourceFileSystem(), MsOptions.Create(_kit.Options),
                MsOptions.Create(new UploadOptions()), new ExtractionService(db, ReaderRegistry.Default(), _kit.Time),
                ReaderRegistry.Default(), _kit.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<QueueProcessor>.Instance);
            _kit.Options.SourceRoot = _kit.SourceRoot;
            _kit.Options.OutputRoot = _kit.OutputRoot;
            while (await processor.ProcessNextBatchAsync(CancellationToken.None)) { }
        }
        Assert.Equal(FileStatus.NeedsReview, _kit.Files().Single(f => f.Extension == "xlsx").Status);

        await using (var db = _kit.CreateDb())
        {
            var svc = new ColumnTemplateService(db, _kit.Time);
            await svc.SaveAsync(Request(), "u", CancellationToken.None);
            Assert.Equal(1, await svc.RequeueUnmappedAsync("CORP\\m", CancellationToken.None));
        }

        await using var check = _kit.CreateDb();
        Assert.Equal(FileStatus.Pending, _kit.Files().Single(f => f.Extension == "xlsx").Status);
        var queued = await check.ProcessingQueue.Where(q => q.Status == QueueItemStatus.Queued).Select(q => q.RelativePath).ToListAsync();
        Assert.Equal(["محاسبة/2024"], queued);
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
