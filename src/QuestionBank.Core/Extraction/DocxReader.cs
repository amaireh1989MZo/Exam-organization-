using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace QuestionBank.Core.Extraction;

/// <summary>
/// قارئ Word (DOCX) عام. يحوّل المستند إلى أسطر مع تنسيقها، ثم يمررها للمحلل المشترك مع TXT.
/// - الترقيم المكتوب في النص ("1." و"أ)") والترقيم التلقائي في Word (الرقم ليس جزءًا من النص) كلاهما مدعوم.
/// - تنسيق الخيار (عريض، لون، تسطير، تظليل) يُسجَّل دليلًا على الإجابة إذا ميّز خيارًا واحدًا فقط.
/// - النص المحذوف بتعقب التغييرات لا يُقرأ. الجداول تُقرأ صفًا صفًا كأسطر.
/// هذا قارئ عام مبني على أنماط Word الشائعة، ويحتاج معايرة على عينات المؤسسة.
/// </summary>
public sealed partial class DocxReader : IQuestionReader
{
    public string Name => "docx";
    public string Version => "0.9";
    public bool CanRead(string extension) => extension == "docx";

    private static readonly string[] ArabicLetters = ["أ", "ب", "ج", "د", "هـ", "و", "ز", "ح"];
    private static readonly string[] LatinLetters = ["a", "b", "c", "d", "e", "f", "g", "h"];

    public ReaderResult Read(byte[] content)
    {
        WordprocessingDocument doc;
        try
        {
            doc = WordprocessingDocument.Open(new MemoryStream(content), false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException)
        {
            throw new FormatException("ملف Word تالف أو ليس DOCX.", ex);
        }

        using (doc)
        {
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null)
                return new ReaderResult(Name, Version, [], ["المستند فارغ."]);

            var numbering = new NumberingResolver(doc.MainDocumentPart!.NumberingDefinitionsPart?.Numbering);
            var lines = new List<SourceLine>();
            var state = new ListState();
            var paragraphNo = 0;
            var tableNo = 0;

            foreach (var element in body.ChildElements)
            {
                switch (element)
                {
                    case Paragraph p:
                        paragraphNo++;
                        AddParagraph(p, $"فقرة {paragraphNo}", numbering, state, lines);
                        break;
                    case Table t:
                        tableNo++;
                        var rowNo = 0;
                        foreach (var row in t.Elements<TableRow>())
                        {
                            rowNo++;
                            foreach (var p in row.Descendants<Paragraph>())
                                AddParagraph(p, $"جدول {tableNo} صف {rowNo}", numbering, state, lines);
                        }
                        break;
                }
            }

            var (questions, warnings) = LineQuestionParser.Parse(lines);
            return new ReaderResult(Name, Version, questions, warnings);
        }
    }

    private sealed class ListState
    {
        public readonly Dictionary<(int NumId, int Level), int> Counters = new();
        public int OptionIndex;
    }

    private static void AddParagraph(Paragraph p, string location, NumberingResolver numbering, ListState state, List<SourceLine> lines)
    {
        var (text, marks) = TextAndMarks(p);
        if (text.Trim().Length == 0)
            return;

        var numPr = p.ParagraphProperties?.NumberingProperties;
        var numId = numPr?.NumberingId?.Val?.Value;
        var level = numPr?.NumberingLevelReference?.Val?.Value ?? 0;
        if (numId is { } id and > 0)
        {
            var format = numbering.FormatOf(id, level);
            if (format is not null && format != "bullet" && format != "none")
            {
                var counter = state.Counters.GetValueOrDefault((id, level)) + 1;
                state.Counters[(id, level)] = counter;
                foreach (var deeper in state.Counters.Keys.Where(k => k.NumId == id && k.Level > level).ToList())
                    state.Counters.Remove(deeper);

                if (level == 0 && IsDecimal(format))
                {
                    // ترقيم تلقائي للسؤال: نكتب الرقم كما يظهر للمستخدم في Word.
                    state.OptionIndex = 0;
                    lines.Add(new SourceLine($"{counter}. {text}", location, marks));
                    return;
                }
                // ترقيم تلقائي للخيار: الحرف حسب ترتيب الخيار داخل السؤال الحالي.
                var letters = ContainsArabic(text) || format.StartsWith("arabic", StringComparison.OrdinalIgnoreCase) ? ArabicLetters : LatinLetters;
                var label = state.OptionIndex < letters.Length ? letters[state.OptionIndex] : (state.OptionIndex + 1).ToString();
                state.OptionIndex++;
                lines.Add(new SourceLine($"{label}) {text}", location, marks));
                return;
            }
        }

        if (LiteralQuestion().IsMatch(TextDecoding.NormalizeDigits(text)))
            state.OptionIndex = 0;
        lines.Add(new SourceLine(text, location, marks));
    }

    /// <summary>
    /// نص الفقرة مع تنسيقها: التنسيق يُعتبر موجودًا إذا غطى 80% على الأقل من الأحرف غير الفارغة،
    /// حتى لا يُحسب خيار فيه كلمة عريضة واحدة كأنه "خيار عريض".
    /// </summary>
    private static (string Text, FormatMarks Marks) TextAndMarks(Paragraph p)
    {
        var sb = new StringBuilder();
        var counts = new Dictionary<FormatMarks, int>();
        var total = 0;
        foreach (var run in p.Descendants<Run>())
        {
            var runText = new StringBuilder();
            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case Text t: runText.Append(t.Text); break;
                    case TabChar: runText.Append(' '); break;
                    case Break or CarriageReturn: runText.Append(' '); break;
                }
            }
            if (runText.Length == 0)
                continue;
            sb.Append(runText);

            var visible = runText.ToString().Count(c => !char.IsWhiteSpace(c));
            total += visible;
            var props = run.RunProperties;
            foreach (var flag in new[] { FormatMarks.Bold, FormatMarks.Color, FormatMarks.Underline, FormatMarks.Highlight })
            {
                if (Has(props, flag))
                    counts[flag] = counts.GetValueOrDefault(flag) + visible;
            }
        }

        var marks = FormatMarks.None;
        if (total > 0)
        {
            foreach (var (flag, count) in counts)
            {
                if (count >= total * 0.8)
                    marks |= flag;
            }
        }
        return (sb.ToString(), marks);
    }

    private static bool Has(RunProperties? props, FormatMarks flag)
    {
        if (props is null)
            return false;
        return flag switch
        {
            FormatMarks.Bold => (props.Bold is { } b && (b.Val is null || b.Val.Value))
                                || (props.BoldComplexScript is { } bc && (bc.Val is null || bc.Val.Value)),
            FormatMarks.Underline => props.Underline?.Val is { } u && u.Value != UnderlineValues.None,
            FormatMarks.Color => props.Color?.Val?.Value is { } c && !string.Equals(c, "auto", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(c, "000000", StringComparison.Ordinal),
            FormatMarks.Highlight => (props.Highlight?.Val is { } h && h.Value != HighlightColorValues.None)
                                     || (props.Shading?.Fill?.Value is { } f && !string.Equals(f, "auto", StringComparison.OrdinalIgnoreCase)
                                         && !string.Equals(f, "FFFFFF", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static bool IsDecimal(string format) =>
        format.StartsWith("decimal", StringComparison.OrdinalIgnoreCase) || format is "hindiNumbers" or "arabicNumbers";

    private static bool ContainsArabic(string s) => s.Any(c => c is >= '\u0600' and <= '\u06FF');

    [GeneratedRegex(@"^\s*(?:(?:س|سؤال|Q)\s*)?\d{1,4}\s*[-.)\u066B:/]\s*\S")]
    private static partial Regex LiteralQuestion();

    /// <summary>يحدد صيغة الترقيم (decimal، lowerLetter، bullet...) لمستوى معين من قائمة Word.</summary>
    private sealed class NumberingResolver(Numbering? numbering)
    {
        private readonly Dictionary<(int, int), string?> _cache = new();

        public string? FormatOf(int numId, int level)
        {
            if (_cache.TryGetValue((numId, level), out var cached))
                return cached;
            string? result = null;
            var instance = numbering?.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
            var absId = instance?.AbstractNumId?.Val?.Value;
            var abs = numbering?.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == absId);
            var lvl = abs?.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
            if (lvl?.NumberingFormat?.Val?.Value is { } fmt)
                result = ((IEnumValue)fmt).Value;
            _cache[(numId, level)] = result;
            return result;
        }
    }
}
