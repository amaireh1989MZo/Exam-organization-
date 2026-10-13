using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using QuestionBank.Core.Extraction;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace QuestionBank.Tests.Extraction;

/// <summary>ملفات Word وExcel اصطناعية تُنشأ داخل الاختبار بأنماط شائعة.</summary>
public sealed class OfficeReaderTests
{
    // ---------- Word ----------

    private sealed record Para(string Text, bool Bold = false, string? Color = null, bool Underline = false, bool Highlight = false,
        int? NumId = null, int Level = 0, string? Deleted = null);

    private static byte[] Docx(IEnumerable<Para> paras, bool withNumbering = false, IEnumerable<string[]>? tableRows = null)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var p in paras)
            {
                var props = new RunProperties();
                if (p.Bold) props.Append(new Bold());
                if (p.Color is not null) props.Append(new Color { Val = p.Color });
                if (p.Underline) props.Append(new Underline { Val = UnderlineValues.Single });
                if (p.Highlight) props.Append(new Highlight { Val = HighlightColorValues.Yellow });
                var para = new Paragraph();
                if (p.NumId is { } id)
                    para.Append(new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = p.Level }, new NumberingId { Val = id })));
                para.Append(new Run(props, new Text(p.Text) { Space = SpaceProcessingModeValues.Preserve }));
                if (p.Deleted is not null)
                    para.Append(new DeletedRun(new Run(new DeletedText(p.Deleted))));
                body.Append(para);
            }
            if (tableRows is not null)
            {
                var table = new Table();
                foreach (var row in tableRows)
                    table.Append(new TableRow(row.Select(c => new TableCell(new Paragraph(new Run(new Text(c)))))));
                body.Append(table);
            }
            main.Document = new Document(body);
            if (withNumbering)
            {
                var numbering = main.AddNewPart<NumberingDefinitionsPart>();
                numbering.Numbering = new Numbering(
                    new AbstractNum(
                        new Level(new NumberingFormat { Val = NumberFormatValues.Decimal }) { LevelIndex = 0 },
                        new Level(new NumberingFormat { Val = NumberFormatValues.ArabicAbjad }) { LevelIndex = 1 })
                    { AbstractNumberId = 1 },
                    new AbstractNum(new Level(new NumberingFormat { Val = NumberFormatValues.Bullet }) { LevelIndex = 0 }) { AbstractNumberId = 2 },
                    new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
                    new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 2 });
            }
        }
        return ms.ToArray();
    }

    private static ReaderResult ReadDocx(byte[] bytes) => new DocxReader().Read(bytes);

    [Fact]
    public void Docx_LiteralNumbering_WithBoldAnswer_IsMediumConfidence()
    {
        var q = Assert.Single(ReadDocx(Docx([
            new("1. ما عاصمة الأردن؟"), new("أ) إربد"), new("ب) عمّان", Bold: true), new("ج) الزرقاء")])).Questions);

        Assert.Equal("ما عاصمة الأردن؟", q.Text);
        Assert.Equal(["إربد", "عمّان", "الزرقاء"], q.Options);
        Assert.Equal(1, q.CorrectIndex);
        Assert.Equal("format-bold", q.Detector);
        Assert.Equal(ExtractionConfidence.Medium, q.Confidence);
        Assert.Contains("الإجابة محددة بالتنسيق فقط", q.Issues);
        Assert.Contains("فقرة 1", q.Location);
    }

    [Theory]
    [InlineData("FF0000", false, false, "format-color")]
    [InlineData(null, true, false, "format-underline")]
    [InlineData(null, false, true, "format-highlight")]
    public void Docx_OtherFormats_SingleOutTheAnswer(string? color, bool underline, bool highlight, string detector)
    {
        var q = Assert.Single(ReadDocx(Docx([
            new("1. سؤال؟"), new("أ) س"), new("ب) ص"), new("ج) ع", Color: color, Underline: underline, Highlight: highlight)])).Questions);
        Assert.Equal(2, q.CorrectIndex);
        Assert.Equal(detector, q.Detector);
    }

    [Fact]
    public void Docx_FormatOnAllOptions_IsNotEvidence()
    {
        var q = Assert.Single(ReadDocx(Docx([
            new("1. سؤال؟"), new("أ) س", Bold: true), new("ب) ص", Bold: true)])).Questions);
        Assert.Null(q.CorrectIndex);
        Assert.Equal(ExtractionConfidence.Low, q.Confidence);
    }

    [Fact]
    public void Docx_FormatAgreeingWithAnswerKey_IsHighConfidence_AndConflictIsLow()
    {
        var agree = Assert.Single(ReadDocx(Docx([
            new("1. سؤال؟"), new("أ) س"), new("ب) ص", Bold: true), new("الإجابات"), new("1-ب")])).Questions);
        Assert.Equal(1, agree.CorrectIndex);
        Assert.Equal(ExtractionConfidence.High, agree.Confidence);
        Assert.Contains("answer-key", agree.Detector);

        var conflict = Assert.Single(ReadDocx(Docx([
            new("1. سؤال؟"), new("أ) س", Bold: true), new("ب) ص"), new("الإجابة: ب")])).Questions);
        Assert.Null(conflict.CorrectIndex);
        Assert.Equal(AnswerDetectors.Conflict, conflict.Detector);
    }

    [Fact]
    public void Docx_WordAutoNumbering_IsReconstructed_AndOptionLettersRestartPerQuestion()
    {
        var r = ReadDocx(Docx([
            new("ما الوحدة الأساسية للزمن؟", NumId: 1, Level: 0),
            new("الدقيقة", NumId: 1, Level: 1), new("الثانية", NumId: 1, Level: 1, Bold: true), new("الساعة", NumId: 1, Level: 1),
            new("أي مصدر متجدد؟", NumId: 1, Level: 0),
            new("الفحم", NumId: 1, Level: 1), new("الرياح", NumId: 1, Level: 1, Color: "C00000"),
            new("ملاحظة نقطية تُعامل كنص عادي", NumId: 2)], withNumbering: true));

        Assert.Equal(2, r.Questions.Count);
        Assert.Equal("ما الوحدة الأساسية للزمن؟", r.Questions[0].Text);
        Assert.Equal(["الدقيقة", "الثانية", "الساعة"], r.Questions[0].Options);
        Assert.Equal(1, r.Questions[0].CorrectIndex);
        Assert.StartsWith("سؤال 2", r.Questions[1].Location);
        Assert.Equal(1, r.Questions[1].CorrectIndex);
        Assert.Equal("الرياح ملاحظة نقطية تُعامل كنص عادي", r.Questions[1].Options[1]);
    }

    [Fact]
    public void Docx_TrackedDeletions_AreIgnored()
    {
        var q = Assert.Single(ReadDocx(Docx([
            new("1. سؤال؟", Deleted: " نص محذوف"), new("أ) س"), new("*ب) ص")])).Questions);
        Assert.Equal("سؤال؟", q.Text);
    }

    [Fact]
    public void Docx_TablesAreReadRowByRow()
    {
        var q = Assert.Single(ReadDocx(Docx([], tableRows: [["1. سؤال في جدول؟"], ["أ) س"], ["*ب) ص"]])).Questions);
        Assert.Equal(1, q.CorrectIndex);
        Assert.Contains("جدول 1", q.Location);
    }

    [Fact]
    public void Docx_CorruptFile_ThrowsFormatException() =>
        Assert.Throws<FormatException>(() => ReadDocx("ليس ملف وورد"u8.ToArray()));

    // ---------- Excel ----------

    private static byte[] Xlsx(params (string Name, string?[][] Rows)[] sheets)
    {
        var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new X.Workbook(new X.Sheets());
            var sst = wb.AddNewPart<SharedStringTablePart>();
            sst.SharedStringTable = new X.SharedStringTable();
            uint id = 1;
            foreach (var (name, rows) in sheets)
            {
                var part = wb.AddNewPart<WorksheetPart>();
                var data = new X.SheetData();
                for (var r = 0; r < rows.Length; r++)
                {
                    var row = new X.Row { RowIndex = (uint)(r + 1) };
                    for (var c = 0; c < rows[r].Length; c++)
                    {
                        if (rows[r][c] is not { } v) continue;
                        var reference = $"{(char)('A' + c)}{r + 1}";
                        if (double.TryParse(v, out _))
                            row.Append(new X.Cell { CellReference = reference, CellValue = new X.CellValue(v), DataType = X.CellValues.Number });
                        else
                        {
                            sst.SharedStringTable.Append(new X.SharedStringItem(new X.Text(v)));
                            row.Append(new X.Cell { CellReference = reference, CellValue = new X.CellValue((sst.SharedStringTable.Count() - 1).ToString()), DataType = X.CellValues.SharedString });
                        }
                    }
                    data.Append(row);
                }
                part.Worksheet = new X.Worksheet(data);
                wb.Workbook.Sheets!.Append(new X.Sheet { Name = name, SheetId = id++, Id = wb.GetIdOfPart(part) });
            }
        }
        return ms.ToArray();
    }

    private static ReaderResult ReadXlsx(byte[] bytes) => new XlsxReader().Read(bytes);

    [Fact]
    public void Xlsx_ArabicHeaders_AnswerAsLetter()
    {
        var r = ReadXlsx(Xlsx(("أسئلة", [
            ["اختبار محاسب 2024"],
            ["الرمز", "نص السؤال", "أ", "ب", "ج", "د", "الإجابة"],
            ["ACC-1", "ما القيد لشراء أصل نقدًا؟", "من ح/الأصل إلى ح/الصندوق", "العكس", "لا يسجل", null, "أ"],
            ["ACC-2", "أي القوائم تُظهر المركز المالي؟", "الدخل", "الميزانية", "التدفقات", "حقوق الملكية", "ب"]])));

        Assert.Equal(2, r.Questions.Count);
        var q = r.Questions[0];
        Assert.Equal(("ACC-1", "ما القيد لشراء أصل نقدًا؟", 0, "answer-column", ExtractionConfidence.High),
            (q.Code, q.Text, q.CorrectIndex, q.Detector, q.Confidence));
        Assert.Equal(3, q.Options.Count); // الخلية الفارغة ليست خيارًا
        Assert.Equal(1, r.Questions[1].CorrectIndex);
        Assert.Equal("ورقة أسئلة · صف 3", q.Location);
    }

    [Fact]
    public void Xlsx_EnglishHeaders_AnswerAsNumberOrText()
    {
        var r = ReadXlsx(Xlsx(("Bank", [
            ["Question", "Option A", "Option B", "Option C", "Correct Answer"],
            ["Capital of Jordan?", "Irbid", "Amman", "Zarqa", "Amman"],
            ["Renewable source?", "Coal", "Wind", "Oil", "b"]])));

        Assert.Equal(1, r.Questions[0].CorrectIndex);
        Assert.Equal(1, r.Questions[1].CorrectIndex);
    }

    [Fact]
    public void Xlsx_NumberedAlternativeHeaders_AndBadAnswer_AreHandled()
    {
        var r = ReadXlsx(Xlsx(("ورقة1", [
            ["رقم", "نص_السؤال", "البديل 1", "البديل 2", "البديل 3", "الصحيح"],
            ["1", "سؤال أول؟", "س", "ص", "ع", "2"],
            ["2", "سؤال ثانٍ؟", "س", "ص", "ع", "غير موجود"]])));

        Assert.Equal(1, r.Questions[0].CorrectIndex);
        Assert.Null(r.Questions[1].CorrectIndex);
        Assert.Equal(ExtractionConfidence.Low, r.Questions[1].Confidence);
        Assert.Contains(r.Questions[1].Issues, i => i.Contains("لا يطابق"));
    }

    [Fact]
    public void Xlsx_UnknownHeaders_AreReportedForManualMapping()
    {
        var r = ReadXlsx(Xlsx(("بيانات", [["عمود1", "عمود2"], ["x", "y"]]), ("أسئلة", [["السؤال", "أ", "ب", "الإجابة"], ["سؤال؟", "س", "ص", "ب"]])));
        Assert.Single(r.Questions);
        Assert.Contains(r.Warnings, w => w.Contains("«بيانات»") && w.Contains("مطابقة يدوية"));
    }

    [Fact]
    public void Xlsx_CorruptFile_ThrowsFormatException() =>
        Assert.Throws<FormatException>(() => ReadXlsx("ليس ملف إكسل"u8.ToArray()));
}
