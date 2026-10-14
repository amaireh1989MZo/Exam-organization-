using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Data;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Quality;
using QuestionBank.Core.Review;

namespace QuestionBank.Core.Frameworks;

/// <summary>
/// اقتراحات نصية مؤقتة: تشابه جيب التمام بين مقاطع الأحرف (3 أحرف) في السؤال وفي المؤشر، بأوزان IDF على مستوى الإطار.
/// المبرر يذكر الكلمات المشتركة. هذا بديل مبسّط حتى يُشغَّل نموذج التضمين المحلي، ولا يُطبَّق تلقائيًا.
/// </summary>
public sealed class LexicalSuggester : IIndicatorSuggester
{
    public string Method => "lexical";
    public const double MinScore = 0.08;

    public Task<IReadOnlyList<IReadOnlyList<IndicatorSuggestion>>> SuggestManyAsync(
        IReadOnlyList<(string Text, IReadOnlyList<string> Options)> questions, IReadOnlyList<CompetencyIndicator> indicators,
        int top, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IReadOnlyList<IndicatorSuggestion>>>(
            questions.Select(q => Suggest(q.Text, q.Options, indicators, top)).ToList());

    public IReadOnlyList<IndicatorSuggestion> Suggest(string questionText, IReadOnlyList<string> options, IReadOnlyList<CompetencyIndicator> indicators, int top)
    {
        if (indicators.Count == 0)
            return [];
        var docs = indicators.Select(i => Grams($"{i.Name} {i.SubDomain}")).ToList();
        var df = new Dictionary<string, int>();
        foreach (var d in docs)
            foreach (var g in d.Keys)
                df[g] = df.GetValueOrDefault(g) + 1;
        double Idf(string g) => Math.Log(1 + indicators.Count / (double)(1 + df.GetValueOrDefault(g)));

        // نص السؤال أهم من نصوص الخيارات (الخيارات الخاطئة تضيف كلمات لا علاقة لها بالمؤشر).
        var qVec = new Dictionary<string, double>();
        foreach (var (g, c) in Grams(questionText))
            qVec[g] = qVec.GetValueOrDefault(g) + c * Idf(g);
        foreach (var (g, c) in Grams(string.Join(" ", options)))
            qVec[g] = qVec.GetValueOrDefault(g) + 0.4 * c * Idf(g);
        var qNorm = Math.Sqrt(qVec.Values.Sum(v => v * v));
        if (qNorm == 0)
            return [];

        var questionWords = Words(questionText + " " + string.Join(" ", options));
        return indicators.Select((ind, i) =>
            {
                var dVec = docs[i].ToDictionary(kv => kv.Key, kv => kv.Value * Idf(kv.Key));
                var dNorm = Math.Sqrt(dVec.Values.Sum(v => v * v));
                var dot = qVec.Where(kv => dVec.ContainsKey(kv.Key)).Sum(kv => kv.Value * dVec[kv.Key]);
                var score = dNorm == 0 ? 0 : dot / (qNorm * dNorm);
                var shared = Words($"{ind.Name} {ind.SubDomain}").Intersect(questionWords).Take(5).ToList();
                var rationale = shared.Count > 0 ? "كلمات مشتركة: " + string.Join("، ", shared) : "تشابه في مقاطع الأحرف فقط";
                return new IndicatorSuggestion(ind.Id, ind.Code, ind.Name, Math.Round(score, 3), Method, rationale);
            })
            // أقل من 8% ضجيج في التجربة: لا نعرض اقتراحًا قد يضلل المراجع.
            .Where(s => s.Score >= MinScore)
            .OrderByDescending(s => s.Score)
            .Take(top)
            .ToList();
    }

    private static Dictionary<string, int> Grams(string text)
    {
        var n = TextNormalizer.Normalize(text);
        var result = new Dictionary<string, int>();
        foreach (var word in n.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // الكلمات القصيرة وأدوات الربط لا تحمل معنى المؤشر.
            if (word.Length < 3 || StopWords.Contains(word))
                continue;
            var w = $" {LightStem(word)} ";
            for (var i = 0; i + 3 <= w.Length; i++)
            {
                var g = w.Substring(i, 3);
                result[g] = result.GetValueOrDefault(g) + 1;
            }
        }
        return result;
    }

    private static readonly string[] Prefixes = ["وال", "بال", "كال", "فال", "لل", "ال"];
    private static readonly string[] Suffixes = ["ات", "ون", "ين", "ها", "هم", "يه", "ه", "ي"];

    /// <summary>تجذيع خفيف: يزيل أداة التعريف والسوابق واللواحق الشائعة حتى لا تتشابه الكلمات بسببها فقط.</summary>
    public static string LightStem(string word)
    {
        foreach (var p in Prefixes)
            if (word.Length - p.Length >= 3 && word.StartsWith(p, StringComparison.Ordinal)) { word = word[p.Length..]; break; }
        foreach (var s in Suffixes)
            if (word.Length - s.Length >= 3 && word.EndsWith(s, StringComparison.Ordinal)) { word = word[..^s.Length]; break; }
        return word;
    }

    private static readonly HashSet<string> StopWords = ["من", "في", "على", "الى", "عن", "مع", "ما", "هو", "هي", "التي", "الذي", "او", "ان", "اي", "يلي", "مما", "بين", "عند", "the", "and", "of", "to"];

    private static HashSet<string> Words(string text) =>
        TextNormalizer.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w)).Select(LightStem).ToHashSet();
}

public sealed record FrameworkView(long Id, string Name, string Version, string SourceName, int IndicatorCount, string ImportedBy, DateTime ImportedAtUtc);

public sealed record ImportResult(FrameworkView? Framework, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed record TreeIndicator(long Id, string? Code, string Name, int Questions);
public sealed record TreeSub(string? Name, IReadOnlyList<TreeIndicator> Indicators);
public sealed record TreeDomain(string Name, IReadOnlyList<TreeSub> Subs);
public sealed record FrameworkTree(FrameworkView Framework, IReadOnlyList<TreeDomain> Domains, int IndicatorsWithoutQuestions, int ClassifiedQuestions);

public sealed record ClassifyItem(long Id, string Text, IReadOnlyList<string> Options, string Source, string ReviewStatus, IReadOnlyList<IndicatorSuggestion> Suggestions);

public enum ClassifyOutcome { Ok, QuestionNotFound, IndicatorNotFound }

public sealed class FrameworkService(QuestionBankDbContext db, IIndicatorSuggester suggester, TimeProvider time)
{
    public async Task<ImportResult> ImportAsync(byte[] content, string fileName, string name, string version, string user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
            return new ImportResult(null, ["أدخل اسم الإطار وإصداره."], []);
        var parsed = FrameworkImporter.Parse(content);
        if (parsed.Errors.Count > 0)
            return new ImportResult(null, parsed.Errors, parsed.Warnings);

        var sha = Convert.ToHexString(SHA256.HashData(content));
        if (await db.Frameworks.AnyAsync(f => f.Name == name.Trim() && f.Version == version.Trim(), cancellationToken))
            return new ImportResult(null, ["يوجد إطار بالاسم والإصدار نفسيهما. استخدم رقم إصدار جديد."], parsed.Warnings);

        var now = time.GetUtcNow().UtcDateTime;
        var framework = new CompetencyFramework
        {
            Name = name.Trim(), Version = version.Trim(), SourceName = Extraction.UploadService.SafeDisplayName(fileName, 255),
            Sha256 = sha, ImportedBy = user, ImportedAtUtc = now
        };
        db.Frameworks.Add(framework);
        await db.SaveChangesAsync(cancellationToken);
        var order = 0;
        foreach (var p in parsed.Indicators)
        {
            db.Indicators.Add(new CompetencyIndicator
            {
                FrameworkId = framework.Id, Order = ++order, MainDomain = p.MainDomain, SubDomain = p.SubDomain,
                Code = p.Code, Name = p.Name, SourceRow = $"صف {p.RowNo}"
            });
        }
        db.AuditLog.Add(new AuditEntry
        {
            AtUtc = now, User = user, Action = "ImportFramework", EntityType = nameof(CompetencyFramework),
            EntityId = framework.Id, NewValue = $"{framework.Version}:{parsed.Indicators.Count}"
        });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return new ImportResult(new FrameworkView(framework.Id, framework.Name, framework.Version, framework.SourceName,
            parsed.Indicators.Count, user, now), [], parsed.Warnings);
    }

    public async Task<IReadOnlyList<FrameworkView>> ListAsync(CancellationToken cancellationToken) =>
        await db.Frameworks.AsNoTracking().OrderByDescending(f => f.Id)
            .Select(f => new FrameworkView(f.Id, f.Name, f.Version, f.SourceName,
                db.Indicators.Count(i => i.FrameworkId == f.Id), f.ImportedBy, f.ImportedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task<FrameworkTree?> TreeAsync(long frameworkId, CancellationToken cancellationToken)
    {
        var view = (await ListAsync(cancellationToken)).FirstOrDefault(f => f.Id == frameworkId);
        if (view is null)
            return null;
        var indicators = await db.Indicators.AsNoTracking().Where(i => i.FrameworkId == frameworkId).OrderBy(i => i.Order).ToListAsync(cancellationToken);
        var ids = indicators.Select(i => i.Id).ToList();
        var counts = await db.Questions.AsNoTracking()
            .Where(q => q.IndicatorId != null && ids.Contains(q.IndicatorId.Value) && q.ReviewStatus != QuestionReviewStatus.Superseded)
            .GroupBy(q => q.IndicatorId!.Value).Select(g => new { g.Key, C = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.C, cancellationToken);

        // ترتيب المجالات كما ورد في الملف.
        var domains = indicators.GroupBy(i => i.MainDomain).Select(d => new TreeDomain(d.Key,
            d.GroupBy(i => i.SubDomain).Select(s => new TreeSub(s.Key,
                s.Select(i => new TreeIndicator(i.Id, i.Code, i.Name, counts.GetValueOrDefault(i.Id))).ToList())).ToList())).ToList();
        return new FrameworkTree(view, domains, indicators.Count(i => counts.GetValueOrDefault(i.Id) == 0), counts.Values.Sum());
    }

    /// <summary>أسئلة بلا مؤشر (ما عدا المُستبدَلة)، مع أفضل 3 اقتراحات من الإطار المحدد.</summary>
    public async Task<IReadOnlyList<ClassifyItem>> UnclassifiedAsync(long frameworkId, int take, CancellationToken cancellationToken)
    {
        var indicators = await db.Indicators.AsNoTracking().Where(i => i.FrameworkId == frameworkId).ToListAsync(cancellationToken);
        var rows = await db.Questions.AsNoTracking()
            .Where(q => q.IndicatorId == null && q.ReviewStatus != QuestionReviewStatus.Superseded)
            .OrderBy(q => q.Id).Take(Math.Clamp(take, 1, 200))
            .Select(q => new
            {
                q.Id, q.OriginalText, q.OriginalOptionsJson, q.ReviewStatus,
                File = db.SourceFiles.Where(f => f.Id == q.SourceFileId).Select(f => f.RelativePath).FirstOrDefault(),
                Upload = db.Uploads.Where(u => u.Id == q.UploadId).Select(u => u.ClientPath ?? u.OriginalName).FirstOrDefault()
            }).ToListAsync(cancellationToken);
        var parsed = rows.Select(r => (Row: r, Options: (IReadOnlyList<string>)(JsonSerializer.Deserialize<string[]>(r.OriginalOptionsJson) ?? []))).ToList();
        var suggestions = await suggester.SuggestManyAsync(parsed.Select(p => (p.Row.OriginalText, p.Options)).ToList(), indicators, 3, cancellationToken);
        return parsed.Select((p, i) => new ClassifyItem(p.Row.Id, p.Row.OriginalText, p.Options,
            p.Row.File ?? ("رفع يدوي/" + p.Row.Upload), p.Row.ReviewStatus.ToString(), suggestions[i])).ToList();
    }

    /// <summary>يربط السؤال بمؤشر موجود فعلًا في إطار مستورد، أو يلغي الربط (indicatorId = null). كل ربط يُسجَّل.</summary>
    public async Task<ClassifyOutcome> AssignAsync(long questionId, long? indicatorId, string method, double? score, string user, CancellationToken cancellationToken)
    {
        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == questionId, cancellationToken);
        if (q is null)
            return ClassifyOutcome.QuestionNotFound;
        CompetencyIndicator? indicator = null;
        if (indicatorId is not null)
        {
            indicator = await db.Indicators.AsNoTracking().FirstOrDefaultAsync(i => i.Id == indicatorId, cancellationToken);
            if (indicator is null)
                return ClassifyOutcome.IndicatorNotFound; // لا مؤشرات خارج الأطر المستوردة
        }

        // الطريقة من قائمة معروفة فقط: يدوي، أو اختيار اقتراح من مزوّد الاقتراحات الحالي.
        method = method == suggester.Method || method == "lexical" ? method : "manual";
        var old = q.IndicatorId;
        var now = time.GetUtcNow().UtcDateTime;
        q.IndicatorId = indicator?.Id;
        q.FrameworkId = indicator?.FrameworkId;
        q.MatchStatus = indicator is null ? MatchStatus.None : MatchStatus.Matched;
        q.MatchMethod = indicator is null ? null : method;
        q.MatchScore = indicator is null ? null : score;
        q.MatchedBy = indicator is null ? null : user;
        q.MatchedAtUtc = indicator is null ? null : now;
        db.AuditLog.Add(new AuditEntry
        {
            AtUtc = now, User = user, Action = indicator is null ? "Unclassify" : "Classify", EntityType = nameof(Question),
            EntityId = q.Id, OldValue = old?.ToString(), NewValue = indicator is null ? null : $"{indicator.Id}:{method}"
        });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return ClassifyOutcome.Ok;
    }
}
