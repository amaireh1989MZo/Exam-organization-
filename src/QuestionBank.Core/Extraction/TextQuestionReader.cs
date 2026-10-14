using System.Text.RegularExpressions;

namespace QuestionBank.Core.Extraction;

/// <summary>تنسيق نص الخيار في المصدر (Word). يُستخدم دليلًا على الإجابة فقط إذا ميّز خيارًا واحدًا.</summary>
[Flags]
public enum FormatMarks
{
    None = 0,
    Bold = 1,
    Color = 2,
    Underline = 4,
    Highlight = 8
}

/// <summary>
/// سطر من المصدر بعد استخراجه: نصه، وموقعه للعرض، وتنسيقه إن وُجد، وصوره (Word)،
/// وهل فيه معادلة Word لم تُقرأ نصًا.
/// </summary>
public sealed record SourceLine(string Text, string Location, FormatMarks Marks = FormatMarks.None,
    IReadOnlyList<ExtractedImage>? Images = null, bool HasMath = false, IReadOnlyList<string>? Notes = null)
{
    public bool HasImages => Images is { Count: > 0 };
}

/// <summary>
/// محلل الأسطر المشترك بين TXT وWord. يكشف الإجابة الصحيحة بطرق مستقلة ويقارن بينها:
/// علامة على الخيار (* أو ✓ أو (صح))، وسطر "الإجابة: ب"، وجدول إجابات في آخر الملف،
/// وتنسيق يميّز خيارًا واحدًا فقط (عريض، لون، تسطير، تظليل).
/// اتفاق أكثر من طريقة يؤكد الإجابة، والتعارض يُحال للمراجع. التنسيق وحده يعطي ثقة متوسطة.
/// </summary>
public static partial class LineQuestionParser
{
    private sealed class Block
    {
        public required string Number;
        public required string Location;
        public readonly List<string> Stem = [];
        public readonly List<(string Label, string Text, bool Marked, FormatMarks Format)> Options = [];
        public string? AnswerLineLabel;
        public readonly List<ExtractedImage> Images = [];
        public bool ImageAfterOptions;
        public bool HasMath;
        public readonly List<string> Notes = [];

        /// <summary>صور السطر تتبع نص السؤال قبل الخيارات، أو الخيار الأخير بعدها.</summary>
        public void Attach(SourceLine line, int? option)
        {
            HasMath |= line.HasMath;
            if (line.Notes is not null)
                Notes.AddRange(line.Notes.Where(n => !Notes.Contains(n)));
            if (!line.HasImages)
                return;
            foreach (var img in line.Images!)
                Images.Add(img with { OptionIndex = option });
        }
    }

    public static (List<ExtractedQuestion> Questions, List<string> Warnings) Parse(IEnumerable<SourceLine> lines)
    {
        var blocks = new List<Block>();
        var key = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        Block? current = null;
        var inKey = false;

        foreach (var line in lines)
        {
            var raw = line.Text.Trim().TrimStart('\uFEFF');
            if (raw.Length == 0)
            {
                // فقرة صورة فقط: تتبع نص السؤال، أو الخيار الأخير إن جاءت بعد الخيارات (موضع غير مؤكد).
                if ((line.HasImages || line.HasMath || line.Notes is { Count: > 0 }) && current is not null && !inKey)
                {
                    var option = current.Options.Count == 0 ? (int?)null : current.Options.Count - 1;
                    current.ImageAfterOptions |= option is not null && line.HasImages;
                    current.Attach(line, option);
                }
                continue;
            }
            // الأرقام تُوحَّد للمطابقة فقط؛ الطول لا يتغير فتبقى مواضع النص الأصلي صحيحة.
            var norm = TextDecoding.NormalizeDigits(raw);

            if (KeyHeader().IsMatch(norm))
            {
                inKey = true;
                current = null;
                continue;
            }
            if (inKey)
            {
                foreach (Match m in KeyPair().Matches(norm))
                    key[m.Groups["n"].Value] = NormalizeLabel(m.Groups["l"].Value);
                continue;
            }

            var q = QuestionStart().Match(norm);
            if (q.Success)
            {
                current = new Block { Number = q.Groups["n"].Value, Location = line.Location };
                current.Stem.Add(raw[q.Groups["t"].Index..].Trim());
                current.Attach(line, null);
                blocks.Add(current);
                continue;
            }
            if (current is null)
                continue; // مقدمة الملف قبل أول سؤال

            var a = AnswerLine().Match(norm);
            if (a.Success)
            {
                current.AnswerLineLabel = NormalizeLabel(a.Groups["l"].Value);
                continue;
            }

            var o = OptionLine().Match(norm);
            if (o.Success)
            {
                var marked = o.Groups["m1"].Success || o.Groups["m2"].Success;
                var optionText = raw.Substring(o.Groups["t"].Index, o.Groups["t"].Length).Trim();
                current.Options.Add((NormalizeLabel(o.Groups["l"].Value), optionText, marked, line.Marks));
                current.Attach(line, current.Options.Count - 1);
                continue;
            }

            // خيار صورته هي نصه: "أ)" ثم صورة فقط.
            var imageOption = line.HasImages ? ImageOnlyOption().Match(norm) : Match.Empty;
            if (imageOption.Success)
            {
                var marked = imageOption.Groups["m1"].Success || imageOption.Groups["m2"].Success;
                current.Options.Add((NormalizeLabel(imageOption.Groups["l"].Value), "", marked, line.Marks));
                current.Attach(line, current.Options.Count - 1);
                continue;
            }

            // سطر تكملة: للنص قبل الخيارات، أو لآخر خيار بعدها.
            if (current.Options.Count == 0)
            {
                current.Stem.Add(raw);
                current.Attach(line, null);
            }
            else
            {
                var last = current.Options[^1];
                current.Options[^1] = (last.Label, (last.Text + " " + raw).Trim(), last.Marked, last.Format);
                current.ImageAfterOptions |= line.HasImages;
                current.Attach(line, current.Options.Count - 1);
            }
        }

        var questions = new List<ExtractedQuestion>();
        foreach (var block in blocks)
        {
            if (block.Options.Count == 0)
            {
                warnings.Add($"{block.Location}: نص مرقّم بلا خيارات، تم تخطيه.");
                continue;
            }
            questions.Add(Build(block, questions.Count + 1, key));
        }
        return (questions, warnings);
    }

    private static readonly (FormatMarks Flag, string Detector)[] FormatDetectors =
    [
        (FormatMarks.Bold, "format-bold"), (FormatMarks.Color, "format-color"),
        (FormatMarks.Underline, "format-underline"), (FormatMarks.Highlight, "format-highlight")
    ];

    private static ExtractedQuestion Build(Block block, int order, Dictionary<string, string> key)
    {
        var issues = new List<string>();
        var options = block.Options.Select(o => o.Text).ToList();
        int? IndexOfLabel(string? label) =>
            label is null ? null : block.Options.FindIndex(o => o.Label == label) is var idx and >= 0 ? idx : null;

        var votes = new List<(string Detector, int Index, bool Strong)>();

        var marked = block.Options.Select((o, i) => (o, i)).Where(x => x.o.Marked).Select(x => x.i).ToList();
        if (marked.Count > 1)
            issues.Add("أكثر من خيار عليه علامة");
        foreach (var m in marked)
            votes.Add((AnswerDetectors.Marker, m, true));

        if (block.AnswerLineLabel is not null)
        {
            if (IndexOfLabel(block.AnswerLineLabel) is { } idx) votes.Add((AnswerDetectors.AnswerLine, idx, true));
            else issues.Add($"سطر الإجابة يشير إلى «{block.AnswerLineLabel}» ولا يوجد خيار بهذا الحرف");
        }
        if (key.TryGetValue(block.Number, out var keyLabel))
        {
            if (IndexOfLabel(keyLabel) is { } idx) votes.Add((AnswerDetectors.AnswerKey, idx, true));
            else issues.Add($"جدول الإجابات يشير إلى «{keyLabel}» ولا يوجد خيار بهذا الحرف");
        }

        // التنسيق دليل فقط إذا ميّز خيارًا واحدًا؛ إذا كانت كل الخيارات (أو عدة خيارات) بالتنسيق نفسه فليس دليلًا.
        if (block.Options.Count >= 2)
        {
            foreach (var (flag, detector) in FormatDetectors)
            {
                var withFlag = block.Options.Select((o, i) => (o, i)).Where(x => x.o.Format.HasFlag(flag)).Select(x => x.i).ToList();
                if (withFlag.Count == 1)
                    votes.Add((detector, withFlag[0], false));
            }
        }

        var distinct = votes.Select(v => v.Index).Distinct().ToList();
        int? correct = null;
        string detectorName;
        var confidence = ExtractionConfidence.High;
        if (distinct.Count == 0)
        {
            detectorName = AnswerDetectors.None;
            confidence = ExtractionConfidence.Low;
            issues.Add("لم يُعثر على علامة للإجابة الصحيحة");
        }
        else if (distinct.Count > 1)
        {
            detectorName = AnswerDetectors.Conflict;
            confidence = ExtractionConfidence.Low;
            issues.Add("تعارض بين طرق تحديد الإجابة: " + string.Join("، ", votes.Select(v => $"{v.Detector}={block.Options[v.Index].Label}")));
        }
        else
        {
            correct = distinct[0];
            detectorName = string.Join("+", votes.Select(v => v.Detector).Distinct());
            if (!votes.Any(v => v.Strong))
            {
                confidence = ExtractionConfidence.Medium;
                issues.Add("الإجابة محددة بالتنسيق فقط");
            }
        }

        if (options.Count < 2)
        {
            issues.Add("أقل من خيارين");
            confidence = ExtractionConfidence.Low;
        }
        // خيار بلا نص مقبول إذا كانت صورته هي محتواه. التكرار يُقارن النص مع بصمات الصور.
        var images = block.Images;
        var identities = options.Select((o, i) => o + "|" + string.Join(",", images.Where(m => m.OptionIndex == i)
            .Select(m => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(m.Content))))).ToList();
        if (options.Where((o, i) => o.Length == 0 && !images.Any(m => m.OptionIndex == i)).Any())
        {
            issues.Add("خيار فارغ");
            confidence = ExtractionConfidence.Low;
        }
        if (images.Count > 0)
        {
            // السؤال المصوّر يُراجع بشريًا دائمًا: يجب التأكد أن كل صورة في مكانها.
            issues.Add($"سؤال مصوّر ({images.Count} صورة): تحقق من موضع الصور");
            if (block.ImageAfterOptions)
                issues.Add("صورة بعد الخيارات نُسبت إلى الخيار الأخير");
            if (images.Any(m => !ExtractedImage.IsDisplayable(m.ContentType)))
                issues.Add("صورة بصيغة لا يعرضها المتصفح ولا Moodle (مثل EMF أو WMF): تحتاج تحويلًا");
            if (confidence == ExtractionConfidence.High) confidence = ExtractionConfidence.Medium;
        }
        foreach (var note in block.Notes)
        {
            issues.Add(note);
            confidence = ExtractionConfidence.Low;
        }
        if (block.HasMath)
        {
            issues.Add("يحتوي معادلة Word لم تُقرأ نصًا: قارن بالملف الأصلي");
            if (confidence == ExtractionConfidence.High) confidence = ExtractionConfidence.Medium;
        }
        if (identities.Count != identities.Distinct().Count())
        {
            issues.Add("خيارات مكررة");
            if (confidence == ExtractionConfidence.High) confidence = ExtractionConfidence.Medium;
        }

        return new ExtractedQuestion(order, null, string.Join("\n", block.Stem).Trim(), options, correct,
            detectorName, confidence, $"سؤال {block.Number} · {block.Location}", issues,
            images.Count == 0 ? null : images.ToList());
    }

    /// <summary>يوحّد الحروف المتكافئة: "ا" = "أ"، و"ه" = "هـ"، واللاتينية بأحرف صغيرة.</summary>
    public static string NormalizeLabel(string label) => label switch
    {
        "ا" or "إ" or "آ" => "أ",
        "ه" => "هـ",
        _ => label.ToLowerInvariant()
    };

    private const string Letters = @"هـ|[أاإآبجدهوa-fA-F]";

    [GeneratedRegex(@"^(?:(?:س|سؤال|Q)\s*)?(?<n>\d{1,4})\s*[-.)\u066B:/]\s*(?<t>\S.*)$")]
    private static partial Regex QuestionStart();

    [GeneratedRegex(@"^(?<m1>[*✓✔]\s*)?\(?(?<l>" + Letters + @")\s*[).\-:]\s*(?<t>.+?)\s*(?<m2>[*✓✔]|\((?:صح|صحيح)\))?$")]
    private static partial Regex OptionLine();

    [GeneratedRegex(@"^(?<m1>[*✓✔]\s*)?\(?(?<l>" + Letters + @")\s*[).\-:]\s*(?<m2>[*✓✔]|\((?:صح|صحيح)\))?$")]
    private static partial Regex ImageOnlyOption();

    [GeneratedRegex(@"^(?:الإجابة الصحيحة|الاجابة الصحيحة|الإجابة|الاجابة|الجواب|Answer)\s*[:\-]\s*\(?(?<l>" + Letters + @")\)?\s*\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex AnswerLine();

    [GeneratedRegex(@"^(?:#+\s*)?(?:مفتاح الإجابات?|مفتاح الاجابات?|الإجابات الصحيحة|الاجابات الصحيحة|الإجابات|الاجابات|Answer\s*Key|Answers)\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyHeader();

    [GeneratedRegex(@"(?<n>\d{1,4})\s*[-.):=]\s*(?<l>" + Letters + @")(?![\p{L}])")]
    private static partial Regex KeyPair();
}

/// <summary>قارئ الملفات النصية (TXT وMD) للأسئلة المرقّمة، عبر المحلل المشترك.</summary>
public sealed class TextQuestionReader : IQuestionReader
{
    public string Name => "text";
    public string Version => "1.1";
    public bool CanRead(string extension) => extension is "txt" or "md";

    public ReaderResult Read(byte[] content)
    {
        var (text, _) = TextDecoding.Decode(content);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select((t, i) => new SourceLine(t, $"سطر {i + 1}"));
        var (questions, warnings) = LineQuestionParser.Parse(lines);
        return new ReaderResult(Name, Version, questions, warnings);
    }
}
