using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using QuestionBank.Core.Quality;

namespace QuestionBank.Core.Frameworks;

public sealed record ParsedIndicator(string MainDomain, string? SubDomain, string? Code, string Name, int RowNo);

public sealed record FrameworkParseResult(IReadOnlyList<ParsedIndicator> Indicators, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// يقرأ إطار الكفايات من Excel. يبحث عن صف العناوين في أول 15 صفًا (ملفات الأطر تبدأ غالبًا بصفوف بيانات وصفية)،
/// ويتعرف على أعمدة: المجال الرئيسي، والمجال الفرعي، ورمز المؤشر، ونص المؤشر.
/// الخلايا الفارغة في أعمدة المجالات تأخذ قيمة الصف السابق (الخلايا المدمجة شائعة في الأطر).
/// النصوص تُحفظ حرفيًا؛ التطبيع للتعرف على العناوين فقط.
/// </summary>
public static class FrameworkImporter
{
    private const int HeaderSearchRows = 15;

    private static readonly HashSet<string> MainHeaders = ["المجال الرييسي", "المجال", "المحور", "المحور الرييسي", "المعيار", "domain", "main domain", "standard"];
    private static readonly HashSet<string> SubHeaders = ["المجال الفرعي", "الكفايه", "الكفايه الفرعيه", "المحور الفرعي", "المعيار الفرعي", "subdomain", "sub domain", "competency"];
    private static readonly HashSet<string> CodeHeaders = ["رمز الموشر", "الرمز", "رقم الموشر", "كود الموشر", "code", "indicator code", "id"];
    private static readonly HashSet<string> NameHeaders = ["الموشر", "نص الموشر", "موشر الاداء", "موشرات الاداء", "الموشرات", "indicator", "indicator name", "performance indicator"];

    public static FrameworkParseResult Parse(byte[] content)
    {
        SpreadsheetDocument doc;
        try
        {
            doc = SpreadsheetDocument.Open(new MemoryStream(content), false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException)
        {
            return new FrameworkParseResult([], ["الملف ليس Excel صالحًا (XLSX)."], []);
        }

        using (doc)
        {
            var wb = doc.WorkbookPart;
            var shared = wb?.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
            var errors = new List<string>();
            var warnings = new List<string>();
            var foundHeaders = new List<string>();

            foreach (var sheet in wb?.Workbook?.Sheets?.Elements<Sheet>() ?? [])
            {
                if (sheet.Id?.Value is not { } rid || wb!.GetPartById(rid) is not WorksheetPart part)
                    continue;
                var rows = ReadRows(part, shared);
                var header = rows.Take(HeaderSearchRows).Select(r => (r.RowNo, Map: MapHeader(r.Cells))).FirstOrDefault(h => h.Map is not null);
                if (header.Map is not { } map)
                {
                    foundHeaders.AddRange(rows.Take(HeaderSearchRows).SelectMany(r => r.Cells.Values).Where(v => v.Length < 60).Take(20));
                    continue;
                }

                var result = new List<ParsedIndicator>();
                string? main = null, sub = null;
                foreach (var (rowNo, cells) in rows.Where(r => r.RowNo > header.RowNo))
                {
                    string? Get(int? col) => col is { } c && cells.TryGetValue(c, out var v) && v.Trim().Length > 0 ? v.Trim() : null;
                    main = Get(map.Main) ?? main;
                    sub = map.Sub is null ? null : Get(map.Sub) ?? sub;
                    var name = Get(map.Name);
                    if (name is null)
                        continue;
                    if (main is null)
                    {
                        errors.Add($"صف {rowNo}: مؤشر بلا مجال رئيسي.");
                        continue;
                    }
                    result.Add(new ParsedIndicator(main, sub, Get(map.Code), name, rowNo));
                }

                if (result.Count == 0)
                    errors.Add($"ورقة «{sheet.Name?.Value}»: لا توجد مؤشرات تحت صف العناوين.");
                var dupCodes = result.Where(i => i.Code is not null).GroupBy(i => i.Code!).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (dupCodes.Count > 0)
                    errors.Add("رموز مؤشرات مكررة: " + string.Join("، ", dupCodes.Take(20)));
                if (map.Code is null)
                    warnings.Add("لم يُعثر على عمود رمز المؤشر؛ ستُعرض المؤشرات بأسمائها فقط.");
                var dupNames = result.GroupBy(i => (i.MainDomain, i.SubDomain, i.Name)).Count(g => g.Count() > 1);
                if (dupNames > 0)
                    warnings.Add($"{dupNames} مؤشر بالاسم نفسه تحت المجال نفسه.");
                return new FrameworkParseResult(result, errors, warnings);
            }

            return new FrameworkParseResult([], [
                "لم يُعثر على صف عناوين فيه عمود للمجال الرئيسي وعمود للمؤشر. عناوين موجودة في الملف: " +
                string.Join("، ", foundHeaders.Distinct().Take(15))], []);
        }
    }

    private sealed record HeaderMap(int Main, int? Sub, int? Code, int Name);

    private static HeaderMap? MapHeader(Dictionary<int, string> cells)
    {
        int? main = null, sub = null, code = null, name = null;
        foreach (var (col, raw) in cells.OrderBy(c => c.Key))
        {
            var h = TextNormalizer.Normalize(raw);
            if (sub is null && SubHeaders.Contains(h)) sub = col;
            else if (main is null && MainHeaders.Contains(h)) main = col;
            else if (code is null && CodeHeaders.Contains(h)) code = col;
            else if (name is null && NameHeaders.Contains(h)) name = col;
        }
        return main is not null && name is not null ? new HeaderMap(main.Value, sub, code, name.Value) : null;
    }

    private static List<(int RowNo, Dictionary<int, string> Cells)> ReadRows(WorksheetPart part, string[] shared)
    {
        var result = new List<(int, Dictionary<int, string>)>();
        var implicitRow = 0;
        foreach (var row in part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
        {
            implicitRow = (int?)row.RowIndex?.Value ?? implicitRow + 1;
            var cells = new Dictionary<int, string>();
            var implicitCol = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var col = cell.CellReference?.Value is { } r ? ColumnIndex(r) : implicitCol;
                implicitCol = col + 1;
                var type = cell.DataType?.Value;
                var value = type == CellValues.SharedString
                    ? (int.TryParse(cell.CellValue?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Length ? shared[i] : "")
                    : type == CellValues.InlineString ? cell.InlineString?.InnerText ?? "" : cell.CellValue?.Text ?? "";
                if (value.Length > 0)
                    cells[col] = value;
            }
            if (cells.Count > 0)
                result.Add((implicitRow, cells));
        }
        return result;
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
