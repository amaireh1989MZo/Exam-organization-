using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Export;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Review;
using QuestionBank.Tests.Inventory;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace QuestionBank.Tests.Export;

public sealed class ExportServiceTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    // أسئلة فيها كل الرموز الخطرة على GIFT وXML.
    private const string Txt = """
        1. ما ناتج 2 = 1 + 1 في {المجموعة}؟ اختر: الصحيح ~ دائمًا #1
        أ) نعم \ صحيح
        *ب) <b>لا</b> & ربما ]]> نهاية
        ج) غير ذلك: 50%

        2. سؤال بلا إجابة؟
        أ) س
        ب) ص

        3. سؤال عادي؟
        *أ) أول
        ب) ثانٍ
        """;

    private async Task<List<long>> SeedAsync()
    {
        _kit.WriteFile("إداري/2024/بنك.txt", Txt);
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        var file = await db.SourceFiles.SingleAsync();
        await new ExtractionService(db, ReaderRegistry.Default(), _kit.Time)
            .ExtractAsync(Encoding.UTF8.GetBytes(Txt), "txt", file.Id, null, CancellationToken.None);
        return await db.Questions.OrderBy(q => q.OriginalOrder).Select(q => q.Id).ToListAsync();
    }

    private async Task ApproveAsync(params long[] ids)
    {
        await using var db = _kit.CreateDb();
        var review = new ReviewService(db, _kit.Time);
        foreach (var id in ids)
            Assert.Equal(ReviewOutcome.Ok, await review.ApproveAsync(id, "CORP\\r", CancellationToken.None));
    }

    private async Task<ExportResult> ExportAsync(ExportFormat format, IReadOnlyList<long> ids, bool shuffle = false, bool categories = true)
    {
        _kit.Options.SourceRoot = _kit.SourceRoot;
        _kit.Options.OutputRoot = _kit.OutputRoot;
        await using var db = _kit.CreateDb();
        return await new ExportService(db, MsOptions.Create(_kit.Options), _kit.Time)
            .ExportAsync(new ExportRequest(format, ids, shuffle, categories), "CORP\\m", CancellationToken.None);
    }

    private string ReadExport(ExportResult r) => File.ReadAllText(Path.Combine(_kit.OutputRoot, "exports", r.FileName!), Encoding.UTF8);

    [Fact]
    public async Task OnlyApprovedQuestionsWithAnswers_AreExported_OthersReportedWithReasons()
    {
        var ids = await SeedAsync();
        await ApproveAsync(ids[0]);

        var result = await ExportAsync(ExportFormat.Gift, [ids[0], ids[1], ids[2], 999]);

        Assert.Null(result.ErrorCode);
        Assert.Equal(1, result.Exported);
        Assert.Equal(["غير معتمد", "غير معتمد", "غير موجود"], result.Rejected.Select(r => r.Reason).ToArray());
    }

    [Fact]
    public async Task Gift_EscapesSpecialCharacters_AndRoundTripsThroughTheReader()
    {
        var ids = await SeedAsync();
        await ApproveAsync(ids[0], ids[2]);

        var result = await ExportAsync(ExportFormat.Gift, [ids[2], ids[0]]);
        var gift = ReadExport(result);

        Assert.Contains(@"2 \= 1 + 1 في \{المجموعة\}؟ اختر\: الصحيح \~ دائمًا \#1", gift);
        var parsed = new GiftReader().Read(Encoding.UTF8.GetBytes(gift)).Questions;
        Assert.Equal(2, parsed.Count);
        Assert.Equal("سؤال عادي؟", parsed[0].Text); // ترتيب السلة محفوظ
        Assert.Equal("ما ناتج 2 = 1 + 1 في {المجموعة}؟ اختر: الصحيح ~ دائمًا #1", parsed[1].Text);
        Assert.Equal(["نعم \\ صحيح", "<b>لا</b> & ربما ]]> نهاية", "غير ذلك: 50%"], parsed[1].Options);
        Assert.Equal(1, parsed[1].CorrectIndex);
        Assert.False(gift.StartsWith('﻿'));
    }

    [Fact]
    public async Task CorrectedAnswer_IsWhatGetsExported()
    {
        var ids = await SeedAsync();
        await using (var db = _kit.CreateDb())
        {
            var review = new ReviewService(db, _kit.Time);
            await review.SetAnswerAsync(ids[1], 0, "r", CancellationToken.None);
            await review.ApproveAsync(ids[1], "r", CancellationToken.None);
        }

        var gift = ReadExport(await ExportAsync(ExportFormat.Gift, [ids[1]]));

        Assert.Equal(0, new GiftReader().Read(Encoding.UTF8.GetBytes(gift)).Questions.Single().CorrectIndex);
    }

    [Fact]
    public async Task MoodleXml_IsWellFormed_RtlCdata_CategoriesAndIdnumber()
    {
        var ids = await SeedAsync();
        await ApproveAsync(ids[0], ids[2]);

        var result = await ExportAsync(ExportFormat.MoodleXml, [ids[0], ids[2]]);
        var xml = ReadExport(result);
        var doc = XDocument.Parse(xml);

        var questions = doc.Root!.Elements("question").ToList();
        var category = questions.Single(q => (string?)q.Attribute("type") == "category");
        Assert.Equal("$course$/top/بنك الأسئلة/إداري/2024", category.Element("category")!.Element("text")!.Value);
        var mc = questions.Where(q => (string?)q.Attribute("type") == "multichoice").ToList();
        Assert.Equal(2, mc.Count);
        var first = mc[0];
        Assert.StartsWith("<p dir=\"rtl\">ما ناتج 2 = 1 + 1 في {المجموعة}", first.Element("questiontext")!.Element("text")!.Value);
        var answers = first.Elements("answer").ToList();
        Assert.Equal(["0", "100", "0"], answers.Select(a => (string)a.Attribute("fraction")!).ToArray());
        // النص الذي يحوي ]]> و<b> يبقى نصًا (مهرَّبًا كـ HTML) ولا يكسر الملف.
        Assert.Equal("<p dir=\"rtl\">&lt;b&gt;لا&lt;/b&gt; &amp; ربما ]]&gt; نهاية</p>", answers[1].Element("text")!.Value);
        Assert.Equal("false", (string)first.Element("shuffleanswers")!);
        Assert.Equal("none", (string)first.Element("answernumbering")!);
        Assert.Equal(32, ((string)first.Element("idnumber")!).Length);
    }

    [Fact]
    public async Task MoodleXml_ShuffleAndNoCategoryOptions_AreRespected()
    {
        var ids = await SeedAsync();
        await ApproveAsync(ids[2]);

        var doc = XDocument.Parse(ReadExport(await ExportAsync(ExportFormat.MoodleXml, [ids[2]], shuffle: true, categories: false)));

        Assert.DoesNotContain(doc.Root!.Elements("question"), q => (string?)q.Attribute("type") == "category");
        Assert.Equal("true", (string)doc.Root.Element("question")!.Element("shuffleanswers")!);
    }

    [Fact]
    public async Task Export_IsRecorded_Audited_AndFileIsRetrievable()
    {
        var ids = await SeedAsync();
        await ApproveAsync(ids[2]);

        var result = await ExportAsync(ExportFormat.Gift, [ids[2]]);

        await using var db = _kit.CreateDb();
        var record = await db.Exports.SingleAsync();
        Assert.Equal((1, "CORP\\m"), (record.QuestionCount, record.RequestedBy));
        Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "Export" && a.EntityId == record.Id);
        var file = await new ExportService(db, MsOptions.Create(_kit.Options), _kit.Time).GetFileAsync(record.Id, CancellationToken.None);
        Assert.NotNull(file);
        Assert.StartsWith(Path.Combine(_kit.OutputRoot, "exports"), file.Value.Path);
        Assert.Equal(result.FileName, file.Value.FileName);
    }

    [Fact]
    public async Task EmptyBasket_OrNothingValid_WritesNoFile()
    {
        var ids = await SeedAsync();

        Assert.Equal("Empty", (await ExportAsync(ExportFormat.Gift, [])).ErrorCode);
        Assert.Equal("NothingToExport", (await ExportAsync(ExportFormat.Gift, [ids[1]])).ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_kit.OutputRoot, "exports")));
    }

    [Fact]
    public async Task SupersededQuestions_AreNotExported()
    {
        var ids = await SeedAsync();
        await ApproveAsync(ids[2]);
        await using (var db = _kit.CreateDb())
        {
            var file = await db.SourceFiles.SingleAsync();
            await new ExtractionService(db, ReaderRegistry.Default(), _kit.Time)
                .ExtractAsync(Encoding.UTF8.GetBytes("1. جديد؟\n*أ) س\nب) ص"), "txt", file.Id, null, CancellationToken.None);
        }

        var result = await ExportAsync(ExportFormat.Gift, [ids[2]]);

        Assert.Equal("NothingToExport", result.ErrorCode);
        Assert.Equal("غير معتمد", result.Rejected.Single().Reason);
    }
}
