using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using QuestionBank.Core.Quality;

namespace QuestionBank.Core.Extraction;

/// <summary>
/// قارئ Excel (XLSX) عام بمطابقة أعمدة تلقائية من عناوينها (عربية أو إنجليزية).
/// يبحث عن صف العناوين في أول 10 صفوف من كل ورقة، ويتعرف على: نص السؤال، والخيارات، والإجابة، والرمز.
/// الإجابة تُقبل حرفًا (ب، b) أو رقمًا (2) أو نص الخيار نفسه.
/// الورقة التي لا تُعرف أعمدتها تُذكر في التحذيرات (تحتاج مطابقة يدوية في الواجهة لاحقًا).
/// </summary>
public sealed class XlsxReader : IQuestionReader
{
    public string Name => "xlsx";
    public string Version => "0.9";
    public bool CanRead(string extension) => extension == "xlsx";

    private const int HeaderSearchRows = 10;

    // العناوين بعد التطبيع (TextNormalizer): بلا همزات ولا تاء مربوطة ولا ترقيم، وبأحرف صغيرة.
    private static readonly HashSet<string> StemHeaders =
        ["السوال", "نص السوال", "السوال نص", "question", "question text", "stem", "item", "نص الفقره", "الفقره", "السؤال"];
    private static readonly HashSet<string> AnswerHeaders =
        ["الاجابه", "الاجابه الصحيحه", "الصحيح", "الجواب", "الجواب الصحيح", "مفتاح الاجابه", "answer", "correct", "correct answer", "key", "answer key"];
    private static readonly HashSet<string> CodeHeaders =
        ["الرمز", "رمز السوال", "كود", "code", "id", "question id", "رقم السوال", "المعرف"];

    private static readonly string[][] OptionLetterAliases =
    [
        ["ا", "a", "1"], ["ب", "b", "2"], ["ج", "c", "3"], ["د", "d", "4"], ["ه", "e", "5"], ["و", "f", "6"]
    ];

    public ReaderResult Read(byte[] content)
    {
        SpreadsheetDocument doc;
        try
        {
            doc = SpreadsheetDocument.Open(new MemoryStream(content), false);
        }
        catch (Exception ex) when (ex is DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or InvalidDataException or FileFormatException)
        {
            throw new FormatException("ملف Excel تالف أو ليس XLSX.", ex);
        }

        using (doc)
        {
            var wb = doc.WorkbookPart;
            if (wb?.Workbook?.Sheets is null)
                return new ReaderResult(Name, Version, [], ["المصنف فارغ."]);
            var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];

            var questions = new List<ExtractedQuestion>();
            var warnings = new List<string>();
            foreach (var sheet in wb.Workbook.Sheets.Elements<Sheet>())
            {
                if (sheet.Id?.Value is not { } rid || wb.GetPartById(rid) is not WorksheetPart part)
                    continue;
                var sheetName = sheet.Name?.Value ?? "ورقة";
                var rows = ReadRows(part, shared);
                if (rows.Count == 0)
                    continue;

                var map = FindHeader(rows);
                if (map is null)
                {
                    warnings.Add($"ورقة «{sheetName}»: لم تُعرف أعمدة السؤال والخيارات من العناوين؛ تحتاج مطابقة يدوية.");
                    continue;
                }

                foreach (var (rowNo, cells) in rows.Where(r => r.RowNo > map.HeaderRow))
                {
                    var stem = Cell(cells, map.Stem);
                    if (string.IsNullOrWhiteSpace(stem))
                        continue;
                    questions.Add(BuildQuestion(questions.Count + 1, sheetName, rowNo, cells, map, stem));
                }
            }
            return new ReaderResult(Name, Version, questions, warnings);
        }
    }

    private sealed record ColumnMap(int HeaderRow, int Stem, IReadOnlyList<int> Options, int? Answer, int? Code);

    private static ExtractedQuestion BuildQuestion(int order, string sheet, int rowNo, Dictionary<int, string> cells, ColumnMap map, string stem)
    {
        var issues = new List<string>();
        // نحتفظ بموقع كل خيار غير فارغ لنفهم الإجابة إذا أُعطيت كحرف أو رقم عمود.
        var options = map.Options.Select((col, pos) => (Pos: pos, Text: Cell(cells, col).Trim()))
            .Where(o => o.Text.Length > 0).ToList();
        int? correct = null;
        var detector = AnswerDetectors.None;
        var confidence = ExtractionConfidence.High;

        var answerRaw = map.Answer is { } ac ? Cell(cells, ac).Trim() : "";
        if (answerRaw.Length > 0)
        {
            var norm = TextNormalizer.Normalize(answerRaw);
            var byLetter = Array.FindIndex(OptionLetterAliases, a => a.Contains(norm));
            if (byLetter >= 0 && options.FindIndex(o => o.Pos == byLetter) is var idx and >= 0)
                correct = idx;
            else
            {
                var byText = options.FindIndex(o => TextNormalizer.Normalize(o.Text) == norm);
                if (byText >= 0) correct = byText;
            }
            if (correct is null)
                issues.Add($"عمود الإجابة «{answerRaw}» لا يطابق أي خيار");
            else
                detector = "answer-column";
        }

        if (correct is null)
        {
            confidence = ExtractionConfidence.Low;
            if (answerRaw.Length == 0)
                issues.Add("عمود الإجابة فارغ أو غير موجود");
        }
        if (options.Count < 2)
        {
            issues.Add("أقل من خيارين");
            confidence = ExtractionConfidence.Low;
        }
        var texts = options.Select(o => o.Text).ToList();
        if (texts.Count != texts.Distinct().Count())
        {
            issues.Add("خيارات مكررة");
            if (confidence == ExtractionConfidence.High) confidence = ExtractionConfidence.Medium;
        }

        var code = map.Code is { } cc ? Cell(cells, cc).Trim() : null;
        return new ExtractedQuestion(order, string.IsNullOrEmpty(code) ? null : code, stem.Trim(), texts, correct,
            detector, confidence, $"ورقة {sheet} · صف {rowNo}", issues);
    }

    private static ColumnMap? FindHeader(List<(int RowNo, Dictionary<int, string> Cells)> rows)
    {
        foreach (var (rowNo, cells) in rows.Take(HeaderSearchRows))
        {
            int? stem = null, answer = null, code = null;
            var options = new List<(int Col, int Order)>();
            foreach (var (col, raw) in cells)
            {
                var h = TextNormalizer.Normalize(raw);
                if (h.Length == 0) continue;
                if (stem is null && StemHeaders.Contains(h)) { stem = col; continue; }
                if (answer is null && AnswerHeaders.Contains(h)) { answer = col; continue; }
                if (code is null && CodeHeaders.Contains(h)) { code = col; continue; }
                if (OptionOrder(h) is { } order) options.Add((col, order));
            }
            if (stem is not null && options.Count >= 2)
                return new ColumnMap(rowNo, stem.Value, options.OrderBy(o => o.Order).ThenBy(o => o.Col).Select(o => o.Col).ToList(), answer, code);
        }
        return null;
    }

    /// <summary>ترتيب عمود الخيار من عنوانه: "أ"، "البديل 2"، "الخيار ج"، "Option B"، "Choice 3"، "A"...</summary>
    private static int? OptionOrder(string h)
    {
        var parts = h.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? token = parts.Length switch
        {
            1 => parts[0],
            2 when parts[0] is "البديل" or "بديل" or "الخيار" or "خيار" or "option" or "choice" or "الاختيار" or "اختيار" => parts[1],
            _ => null
        };
        if (token is null)
            return null;
        var index = Array.FindIndex(OptionLetterAliases, a => a.Contains(token));
        // الرقم وحده عنوانًا ("1") قد يكون رقم السؤال؛ نقبل الأرقام فقط مع كلمة مثل "البديل".
        if (index >= 0 && parts.Length == 1 && char.IsDigit(token[0]))
            return null;
        return index >= 0 ? index : null;
    }

    private static string Cell(Dictionary<int, string> cells, int col) => cells.GetValueOrDefault(col, "");

    private static List<(int RowNo, Dictionary<int, string> Cells)> ReadRows(WorksheetPart part, string[] shared)
    {
        var result = new List<(int, Dictionary<int, string>)>();
        var data = part.Worksheet?.GetFirstChild<SheetData>();
        if (data is null)
            return result;
        var implicitRow = 0;
        foreach (var row in data.Elements<Row>())
        {
            implicitRow = (int?)row.RowIndex?.Value ?? implicitRow + 1;
            var cells = new Dictionary<int, string>();
            var implicitCol = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var col = cell.CellReference?.Value is { } r ? ColumnIndex(r) : implicitCol;
                implicitCol = col + 1;
                var value = CellText(cell, shared);
                if (value.Length > 0)
                    cells[col] = value;
            }
            if (cells.Count > 0)
                result.Add((implicitRow, cells));
        }
        return result;
    }

    private static string CellText(Cell cell, string[] shared)
    {
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
            return int.TryParse(cell.CellValue?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Length ? shared[i] : "";
        if (type == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? "";
        if (type == CellValues.Boolean)
            return cell.CellValue?.Text == "1" ? "TRUE" : "FALSE";
        return cell.CellValue?.Text ?? "";
    }

    private static int ColumnIndex(string reference)
    {
        var col = 0;
        foreach (var c in reference)
        {
            if (!char.IsLetter(c)) break;
            col = col * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return col - 1;
    }
}
