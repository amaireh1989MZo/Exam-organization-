using System.Text;
using System.Text.RegularExpressions;

namespace QuestionBank.Core.Extraction;

/// <summary>
/// قارئ Moodle GIFT لأسئلة الاختيار من متعدد وصح/خطأ. الأنواع الأخرى (إجابة قصيرة، مطابقة، رقمية)
/// تُتخطى بتحذير ولا تُحوَّل، لأن البنك يعتمد الاختيار من متعدد.
/// </summary>
public sealed partial class GiftReader : IQuestionReader
{
    public string Name => "gift";
    public string Version => "1.0";
    public bool CanRead(string extension) => extension == "gift";

    public ReaderResult Read(byte[] content)
    {
        var (text, _) = TextDecoding.Decode(content);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var questions = new List<ExtractedQuestion>();
        var warnings = new List<string>();

        var block = new StringBuilder();
        var blockStart = 0;
        for (var i = 0; i <= lines.Length; i++)
        {
            var line = i < lines.Length ? lines[i] : "";
            var trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("$CATEGORY:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (trimmed.Length == 0)
            {
                if (block.Length > 0)
                {
                    ParseBlock(block.ToString(), blockStart + 1, questions, warnings);
                    block.Clear();
                }
                continue;
            }
            if (block.Length == 0)
                blockStart = i;
            else
                block.Append('\n');
            block.Append(line);
        }

        return new ReaderResult(Name, Version, questions, warnings);
    }

    private static void ParseBlock(string block, int lineNumber, List<ExtractedQuestion> questions, List<string> warnings)
    {
        var location = $"سطر {lineNumber}";
        var s = block.Trim();
        string? title = null;
        if (s.StartsWith("::", StringComparison.Ordinal))
        {
            var end = IndexOfUnescaped(s, "::", 2);
            if (end > 0)
            {
                title = Unescape(s[2..end]).Trim();
                s = s[(end + 2)..].TrimStart();
            }
        }
        s = FormatTag().Replace(s, "", 1);

        var open = IndexOfUnescaped(s, "{", 0);
        var close = open < 0 ? -1 : IndexOfUnescaped(s, "}", open + 1);
        if (open < 0 || close < 0)
        {
            warnings.Add($"{location}: نص بلا إجابات بين {{ }}، تم تخطيه.");
            return;
        }

        var before = s[..open].Trim();
        var after = s[(close + 1)..].Trim();
        var stem = Unescape(after.Length > 0 ? $"{before} _____ {after}" : before).Trim();
        var body = s[(open + 1)..close].Trim();

        if (TrueFalse().Match(body) is { Success: true } tf)
        {
            var isTrue = tf.Groups[1].Value.StartsWith('T') || tf.Groups[1].Value.StartsWith('t');
            questions.Add(new ExtractedQuestion(questions.Count + 1, title, stem, ["صواب", "خطأ"], isTrue ? 0 : 1,
                AnswerDetectors.Gift, ExtractionConfidence.High, location, ["سؤال صح/خطأ"]));
            return;
        }
        if (body.StartsWith('#'))
        {
            warnings.Add($"{location}: سؤال رقمي غير مدعوم، تم تخطيه.");
            return;
        }

        var tokens = Tokenize(body);
        if (tokens.Count == 0 || tokens.Any(t => IndexOfUnescaped(t.Text, "->", 0) >= 0))
        {
            warnings.Add($"{location}: سؤال مطابقة أو بنية غير مدعومة، تم تخطيه.");
            return;
        }
        if (tokens.All(t => t.Marker == '='))
        {
            warnings.Add($"{location}: سؤال إجابة قصيرة غير مدعوم، تم تخطيه.");
            return;
        }

        var options = new List<string>();
        var correct = new List<int>();
        var issues = new List<string>();
        foreach (var token in tokens)
        {
            var textPart = token.Text;
            var hash = IndexOfUnescaped(textPart, "#", 0);
            if (hash >= 0)
                textPart = textPart[..hash];

            var weight = token.Marker == '=' ? 100.0 : 0.0;
            var w = Weight().Match(textPart);
            if (w.Success)
            {
                weight = double.Parse(w.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                textPart = textPart[w.Length..];
                if (weight is > 0 and < 100)
                    issues.Add("أوزان جزئية للإجابات");
            }
            if (weight >= 100)
                correct.Add(options.Count);
            options.Add(Unescape(textPart).Trim());
        }

        int? correctIndex = correct.Count == 1 ? correct[0] : null;
        var detector = AnswerDetectors.Gift;
        var confidence = ExtractionConfidence.High;
        if (correct.Count == 0)
        {
            issues.Add("لا توجد إجابة صحيحة كاملة الوزن");
            detector = AnswerDetectors.None;
            confidence = ExtractionConfidence.Low;
        }
        else if (correct.Count > 1)
        {
            issues.Add("أكثر من إجابة صحيحة");
            detector = AnswerDetectors.Conflict;
            confidence = ExtractionConfidence.Low;
        }
        if (options.Any(o => o.Length == 0))
        {
            issues.Add("خيار فارغ");
            confidence = ExtractionConfidence.Low;
        }

        questions.Add(new ExtractedQuestion(questions.Count + 1, title, stem, options, correctIndex,
            detector, confidence, location, issues.Distinct().ToList()));
    }

    private static List<(char Marker, string Text)> Tokenize(string body)
    {
        var tokens = new List<(char, string)>();
        char? marker = null;
        var current = new StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if ((c == '=' || c == '~') && !IsEscaped(body, i) && !(c == '=' && i + 1 < body.Length && body[i + 1] == '>'))
            {
                if (marker is not null)
                    tokens.Add((marker.Value, current.ToString()));
                marker = c;
                current.Clear();
                continue;
            }
            if (marker is not null)
                current.Append(c);
        }
        if (marker is not null)
            tokens.Add((marker.Value, current.ToString()));
        return tokens;
    }

    private static bool IsEscaped(string s, int index)
    {
        var backslashes = 0;
        for (var i = index - 1; i >= 0 && s[i] == '\\'; i--)
            backslashes++;
        return backslashes % 2 == 1;
    }

    private static int IndexOfUnescaped(string s, string value, int start)
    {
        for (var i = s.IndexOf(value, start, StringComparison.Ordinal); i >= 0; i = s.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            if (!IsEscaped(s, i))
                return i;
        }
        return -1;
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var next = s[i + 1];
                if (next == 'n') { sb.Append('\n'); i++; continue; }
                if ("~=#{}:\\".Contains(next)) { sb.Append(next); i++; continue; }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^\[(html|moodle|plain|markdown)\]", RegexOptions.IgnoreCase)]
    private static partial Regex FormatTag();

    [GeneratedRegex(@"^(T|F|TRUE|FALSE)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TrueFalse();

    [GeneratedRegex(@"^\s*%(-?\d+(?:\.\d+)?)%")]
    private static partial Regex Weight();
}
