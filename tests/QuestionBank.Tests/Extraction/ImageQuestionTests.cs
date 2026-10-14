using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Export;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Review;
using QuestionBank.Tests.Inventory;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using M = DocumentFormat.OpenXml.Math;
using MsOptions = Microsoft.Extensions.Options.Options;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using V = DocumentFormat.OpenXml.Vml;
using X = DocumentFormat.OpenXml.Spreadsheet;
using XDR = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace QuestionBank.Tests.Extraction;

/// <summary>الأسئلة المصوّرة: من ملف Word اصطناعي إلى المراجعة والتصدير.</summary>
public sealed class ImageQuestionTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    // صورتا PNG صحيحتان بحجم 1×1، وملف EMF اصطناعي.
    private static readonly byte[] Png1 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private static readonly byte[] Png2 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private static readonly byte[] Emf = [1, 0, 0, 0, 0x6C, 0, 0, 0, 0x20, 0x45, 0x4D, 0x46];

    private static string ImageId(MainDocumentPart main, byte[] bytes, string contentType)
    {
        var part = main.AddImagePart(contentType);
        part.FeedData(new MemoryStream(bytes));
        return main.GetIdOfPart(part);
    }

    private static Drawing DrawingOf(string? embed, string? link = null) =>
        new(new DW.Inline(new A.Graphic(new A.GraphicData(
            new PIC.Picture(new PIC.BlipFill(new A.Blip { Embed = embed, Link = link })))
        { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));

    private static Paragraph P(string text, params OpenXmlElement[] extra)
    {
        var p = new Paragraph();
        if (text.Length > 0)
            p.Append(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        foreach (var e in extra)
            p.Append(e is Run or M.OfficeMath ? e : new Run(e));
        return p;
    }

    /// <summary>
    /// 1: صورة في سطر السؤال، وخيار صورته هي محتواه وعليه علامة الإجابة.
    /// 2: فقرة صورة منفصلة (بنسخة Fallback قديمة يجب ألا تتكرر)، وصورة بعد الخيارات.
    /// 3: معادلة. 4: صورة EMF. 5: صورة مرتبطة بملف خارجي.
    /// </summary>
    private static byte[] PictureDocx()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var png1 = ImageId(main, Png1, "image/png");
            var png2 = ImageId(main, Png2, "image/png");
            var emf = ImageId(main, Emf, "image/x-emf");
            var withFallback = new AlternateContent(
                new AlternateContentChoice(DrawingOf(png1)) { Requires = "wps" },
                new AlternateContentFallback(new Picture(new V.Shape(new V.ImageData { RelationshipId = png1 }))));

            main.Document = new Document(new Body(
                P("1. ما الشكل الظاهر في الصورة؟", DrawingOf(png1)),
                P("أ) مربع"),
                P("*ب) ", DrawingOf(png2)),
                P("ج) دائرة"),
                P("2. سؤال بصورة في فقرة منفصلة؟"),
                P("", withFallback),
                P("أ) نعم"),
                P("*ب) لا"),
                P("", DrawingOf(png2)),
                P("3. احسب قيمة المعادلة", new M.OfficeMath(new M.Run(new M.Text("x²")))),
                P("أ) 1"),
                P("*ب) 2"),
                P("4. صورة قديمة؟", DrawingOf(emf)),
                P("*أ) س"),
                P("ب) ص"),
                P("5. صورة مرتبطة؟", DrawingOf(null, "rId99")),
                P("*أ) س"),
                P("ب) ص")));
        }
        return ms.ToArray();
    }

    [Fact]
    public void Docx_AttachesImagesToStemAndOptions_AndFlagsUncertainCases()
    {
        var qs = new DocxReader().Read(PictureDocx()).Questions;
        Assert.Equal(5, qs.Count);

        var q1 = qs[0];
        Assert.Equal("ما الشكل الظاهر في الصورة؟", q1.Text);
        Assert.Equal(["مربع", "", "دائرة"], q1.Options);
        Assert.Equal(1, q1.CorrectIndex);
        Assert.Equal([(null, Png1), (1, Png2)], q1.Images!.Select(m => ((int?)m.OptionIndex, m.Content)).ToList(), new ImageComparer());
        Assert.Equal(ExtractionConfidence.Medium, q1.Confidence); // المصوّر يُراجع بشريًا دائمًا
        Assert.Contains(q1.Issues, i => i.StartsWith("سؤال مصوّر (2 صورة)"));
        Assert.DoesNotContain("خيار فارغ", q1.Issues);

        var q2 = qs[1];
        Assert.Equal(2, q2.Images!.Count); // نسخة Fallback لا تُحسب صورة ثانية
        Assert.Null(q2.Images[0].OptionIndex);
        Assert.Equal(1, q2.Images[1].OptionIndex);
        Assert.Contains("صورة بعد الخيارات نُسبت إلى الخيار الأخير", q2.Issues);

        Assert.Null(qs[2].Images);
        Assert.Contains(qs[2].Issues, i => i.Contains("معادلة"));
        Assert.Equal(ExtractionConfidence.Medium, qs[2].Confidence);

        Assert.Equal("image/x-emf", Assert.Single(qs[3].Images!).ContentType);
        Assert.Contains(qs[3].Issues, i => i.Contains("EMF"));

        Assert.Null(qs[4].Images);
        Assert.Contains("صورة مرتبطة بملف خارجي وليست داخل المستند", qs[4].Issues);
        Assert.Equal(ExtractionConfidence.Low, qs[4].Confidence);
    }

    [Fact]
    public void EmptyOption_WithoutImage_IsStillAnIssue()
    {
        var lines = new[]
        {
            new SourceLine("1. سؤال؟", "l1"), new SourceLine("أ) نعم", "l2"), new SourceLine("ب) لا", "l3"),
        };
        var (questions, _) = LineQuestionParser.Parse(lines);
        Assert.DoesNotContain("خيار فارغ", Assert.Single(questions).Issues);

        // سطر "ب)" بلا صورة ليس خيارًا: يبقى تكملة ولا يصنع خيارًا فارغًا.
        var (q2, _) = LineQuestionParser.Parse([new SourceLine("1. سؤال؟", "l1"), new SourceLine("أ) نعم", "l2"), new SourceLine("ب)", "l3")]);
        Assert.Single(Assert.Single(q2).Options);
    }

    private async Task<long> ExtractAsync(byte[] docx)
    {
        if (!_kit.Files().Any())
        {
            _kit.WriteFile("مصور/بنك.docx");
            await _kit.ScanAsync();
        }
        await using var db = _kit.CreateDb();
        var file = await db.SourceFiles.SingleAsync();
        var outcome = await new ExtractionService(db, ReaderRegistry.Default(), _kit.Time)
            .ExtractAsync(docx, "docx", file.Id, null, CancellationToken.None);
        Assert.Null(outcome.ErrorCode);
        return file.Id;
    }

    [Fact]
    public async Task Extraction_StoresEachImageOnce_AndReviewListsThem_AndReplacementRemovesLinks()
    {
        await ExtractAsync(PictureDocx());
        await using (var db = _kit.CreateDb())
        {
            Assert.Equal(5, await db.QuestionImages.CountAsync());
            Assert.Equal(3, await db.ImageBlobs.CountAsync()); // Png1 وPng2 تكررتا وحُفظتا مرة واحدة
            Assert.All(await db.Questions.ToListAsync(), q => Assert.Equal(QuestionReviewStatus.NeedsReview, q.ReviewStatus));

            var page = await new ReviewService(db, _kit.Time).ListAsync(null, 0, 50, CancellationToken.None);
            var first = page.Items.Single(i => i.Text.StartsWith("ما الشكل"));
            Assert.Equal(2, first.Images!.Count);
            Assert.All(first.Images, m => Assert.True(m.Displayable));
            Assert.False(page.Items.Single(i => i.Text.StartsWith("صورة قديمة")).Images!.Single().Displayable);
        }

        // الملف تغيّر وصار بلا صور: روابط الأسئلة غير المراجَعة تُحذف معها.
        var plain = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(plain, WordprocessingDocumentType.Document))
            doc.AddMainDocumentPart().Document = new Document(new Body(P("1. سؤال؟"), P("*أ) نعم"), P("ب) لا")));
        await ExtractAsync(plain.ToArray());
        await using (var db = _kit.CreateDb())
        {
            Assert.Equal(0, await db.QuestionImages.CountAsync());
            Assert.Equal(1, await db.Questions.CountAsync());
        }
    }

    [Fact]
    public async Task TextCorrection_AllowsEmptyText_OnlyWhereAnImageIsTheContent()
    {
        await ExtractAsync(PictureDocx());
        await using var db = _kit.CreateDb();
        var q1 = await db.Questions.AsNoTracking().SingleAsync(q => q.OriginalOrder == 1);
        var review = new ReviewService(db, _kit.Time);

        Assert.Equal(ReviewOutcome.Ok, await review.SetTextAsync(q1.Id, "ما الشكل؟", ["مربع", "", "دائرة"], "CORP\\r", CancellationToken.None));
        Assert.Equal(ReviewOutcome.InvalidText, await review.SetTextAsync(q1.Id, "ما الشكل؟", ["", "", "دائرة"], "CORP\\r", CancellationToken.None));
    }

    private async Task<ExportResult> ApproveAndExportAsync(ExportFormat format, params int[] orders)
    {
        await ExtractAsync(PictureDocx());
        List<long> ids;
        await using (var db = _kit.CreateDb())
        {
            ids = await db.Questions.Where(q => orders.Contains(q.OriginalOrder)).OrderBy(q => q.OriginalOrder).Select(q => q.Id).ToListAsync();
            var review = new ReviewService(db, _kit.Time);
            foreach (var id in ids)
                Assert.Equal(ReviewOutcome.Ok, await review.ApproveAsync(id, "CORP\\r", CancellationToken.None));
        }
        _kit.Options.SourceRoot = _kit.SourceRoot;
        _kit.Options.OutputRoot = _kit.OutputRoot;
        await using var db2 = _kit.CreateDb();
        return await new ExportService(db2, MsOptions.Create(_kit.Options), _kit.Time)
            .ExportAsync(new ExportRequest(format, ids, false, false), "CORP\\m", CancellationToken.None);
    }

    [Fact]
    public async Task MoodleXml_EmbedsEachImage_WhereItBelongs()
    {
        var result = await ApproveAndExportAsync(ExportFormat.MoodleXml, 1, 2, 4);

        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Exported);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("EMF"));

        var xml = XDocument.Load(Path.Combine(_kit.OutputRoot, "exports", result.FileName!));
        var q1 = xml.Root!.Elements("question").First(q => (string?)q.Attribute("type") == "multichoice");
        var stemFile = Assert.Single(q1.Element("questiontext")!.Elements("file"));
        Assert.Equal(Png1, Convert.FromBase64String(stemFile.Value));
        Assert.Contains("@@PLUGINFILE@@/" + (string)stemFile.Attribute("name")!, (string)q1.Element("questiontext")!.Element("text")!);

        var answers = q1.Elements("answer").ToList();
        Assert.Equal("100", (string?)answers[1].Attribute("fraction"));
        Assert.Equal(Png2, Convert.FromBase64String(Assert.Single(answers[1].Elements("file")).Value));
        Assert.Empty(answers[0].Elements("file"));
        Assert.DoesNotContain("<p dir", (string)answers[1].Element("text")!); // صورة بلا نص
    }

    [Fact]
    public async Task Gift_RejectsPictureQuestions_WithAClearReason()
    {
        var result = await ApproveAndExportAsync(ExportFormat.Gift, 1, 3);

        Assert.Null(result.ErrorCode);
        Assert.Equal(1, result.Exported); // سؤال المعادلة بلا صور يُصدَّر
        Assert.Contains(result.Rejected, r => r.Reason.Contains("Moodle XML"));
        var gift = File.ReadAllText(Path.Combine(_kit.OutputRoot, "exports", result.FileName!), Encoding.UTF8);
        Assert.DoesNotContain("الشكل", gift);
    }

    // ---------- Excel ----------

    /// <summary>ورقة بعناوين معروفة وصور عائمة: (صف، عمود) يبدأ من 0 كما في ملف الرسم.</summary>
    private static byte[] PictureXlsx(string?[][] rows, params (int Row, int Col, byte[] Bytes)[] pictures)
    {
        var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new X.Workbook(new X.Sheets());
            var part = wb.AddNewPart<WorksheetPart>();
            var data = new X.SheetData();
            for (var r = 0; r < rows.Length; r++)
            {
                var row = new X.Row { RowIndex = (uint)(r + 1) };
                for (var c = 0; c < rows[r].Length; c++)
                {
                    if (rows[r][c] is { } v)
                        row.Append(new X.Cell { CellReference = $"{(char)('A' + c)}{r + 1}", DataType = X.CellValues.InlineString, InlineString = new X.InlineString(new X.Text(v)) });
                }
                data.Append(row);
            }
            part.Worksheet = new X.Worksheet(data);
            var drawings = part.AddNewPart<DrawingsPart>();
            var wsDrawing = new XDR.WorksheetDrawing();
            foreach (var (r, c, bytes) in pictures)
            {
                var image = drawings.AddImagePart("image/png");
                image.FeedData(new MemoryStream(bytes));
                wsDrawing.Append(new XDR.TwoCellAnchor(
                    new XDR.FromMarker(new XDR.ColumnId(c.ToString()), new XDR.ColumnOffset("0"), new XDR.RowId(r.ToString()), new XDR.RowOffset("0")),
                    new XDR.ToMarker(new XDR.ColumnId((c + 1).ToString()), new XDR.ColumnOffset("0"), new XDR.RowId((r + 1).ToString()), new XDR.RowOffset("0")),
                    new XDR.Picture(new XDR.BlipFill(new A.Blip { Embed = drawings.GetIdOfPart(image) })),
                    new XDR.ClientData()));
            }
            drawings.WorksheetDrawing = wsDrawing;
            part.Worksheet.Append(new X.Drawing { Id = part.GetIdOfPart(drawings) });
            wb.Workbook.Sheets!.Append(new X.Sheet { Name = "بنك", SheetId = 1, Id = wb.GetIdOfPart(part) });
        }
        return ms.ToArray();
    }

    [Fact]
    public void Xlsx_FloatingImages_FollowTheirRowAndColumn()
    {
        var bytes = PictureXlsx(
        [
            ["السؤال", "أ", "ب", "ج", "الإجابة"],
            ["ما الشكل؟", "مربع", null, "دائرة", "ب"],   // صورة الخيار ب هي محتواه، وصورة في عمود الإجابة تتبع النص
            [null, "نعم", "لا", null, "أ"],              // نص السؤال صورة فقط
            []                                          // صف فارغ فيه صورة: لا تُنسب لأي سؤال
        ], (1, 2, Png2), (1, 4, Png1), (2, 0, Png1), (3, 1, Png2));

        var result = new XlsxReader().Read(bytes);
        Assert.Equal(2, result.Questions.Count);

        var q1 = result.Questions[0];
        Assert.Equal(["مربع", "", "دائرة"], q1.Options);
        Assert.Equal(1, q1.CorrectIndex);
        Assert.Equal([(1, Png2), (null, Png1)], q1.Images!.Select(m => (m.OptionIndex, m.Content)).ToList(), new ImageComparer());
        Assert.Equal(ExtractionConfidence.Medium, q1.Confidence);

        var q2 = result.Questions[1];
        Assert.Equal("", q2.Text);
        Assert.Null(Assert.Single(q2.Images!).OptionIndex);
        Assert.Equal(0, q2.CorrectIndex);

        Assert.Contains(result.Warnings, w => w.Contains("صف فيه صورة بلا سؤال"));
    }

    private sealed class ImageComparer : IEqualityComparer<(int?, byte[])>
    {
        public bool Equals((int?, byte[]) x, (int?, byte[]) y) => x.Item1 == y.Item1 && x.Item2.SequenceEqual(y.Item2);
        public int GetHashCode((int?, byte[]) obj) => obj.Item1.GetHashCode();
    }
}
