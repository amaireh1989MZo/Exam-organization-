using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Data;
using QuestionBank.Core.Extraction;

namespace QuestionBank.Core.Quality;

public enum DuplicateKind
{
    /// <summary>النص والخيارات نفسها بعد التطبيع (ترتيب الخيارات لا يهم).</summary>
    Exact,

    /// <summary>النص نفسه بخيارات مختلفة: قد تكون نسخة معدّلة أو سؤالًا مختلفًا بالصياغة نفسها.</summary>
    SameStem,

    /// <summary>صياغة مختلفة قليلًا (تشابه نص السؤال ≥ الحد)، وليس تكرارًا حرفيًا ولا النص نفسه.</summary>
    Near
}

public sealed record DuplicateMember(long Id, string Text, IReadOnlyList<string> Options, int? EffectiveCorrectIndex, string ReviewStatus, string Source, string Location);

public sealed record DuplicateGroup(string Key, int Count, bool AnswersDiffer, IReadOnlyList<DuplicateMember> Members, double? Similarity = null);

public sealed record DuplicatePage(int ExactGroups, int SameStemGroups, int Total, IReadOnlyList<DuplicateGroup> Groups, int? NearGroups = null);

/// <summary>
/// كشف التكرار الحرفي، والنص نفسه بخيارات مختلفة. لا يحذف ولا يعدّل أي سؤال؛ يعرض المجموعات للمراجع واللجنة.
/// الأسئلة المُستبدَلة مستبعدة.
/// </summary>
public sealed class DuplicateService(QuestionBankDbContext db)
{
    public const double NearThreshold = 0.8;
    private const int MaxCandidatePairs = 200_000;

    /// <summary>يحسب مفاتيح MinHash للأسئلة التي ليس لها مفاتيح (المستخرجة قبل إضافة الميزة).</summary>
    public async Task<int> BackfillBandsAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        long lastId = 0;
        while (true)
        {
            var batch = await db.Questions.AsNoTracking()
                .Where(q => q.Id > lastId && !db.QuestionBands.Any(b => b.QuestionId == q.Id))
                .OrderBy(q => q.Id).Take(500).ToListAsync(cancellationToken);
            if (batch.Count == 0)
                return total;
            foreach (var q in batch)
                ExtractionService.AddBands(db, q);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            lastId = batch[^1].Id;
            total += batch.Count;
        }
    }

    /// <summary>
    /// مجموعات شبه التكرار: مرشحون من حزم MinHash، ثم تحقق بالتشابه الفعلي، ثم تجميع (اتحاد المجموعات).
    /// يُستبعد ما هو تكرار حرفي أو النص نفسه، لأن لهما فئتين خاصتين.
    /// </summary>
    public async Task<(int Count, List<DuplicateGroup> Groups)> GetNearGroupsAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var activeIds = db.Questions.Where(q => q.ReviewStatus != QuestionReviewStatus.Superseded).Select(q => q.Id);
        var pairs = await (
                from a in db.QuestionBands
                join b in db.QuestionBands on new { a.Band, a.Key } equals new { b.Band, b.Key }
                where a.QuestionId < b.QuestionId && activeIds.Contains(a.QuestionId) && activeIds.Contains(b.QuestionId)
                select new { A = a.QuestionId, B = b.QuestionId })
            .Distinct().Take(MaxCandidatePairs).ToListAsync(cancellationToken);
        if (pairs.Count == 0)
            return (0, []);

        var ids = pairs.SelectMany(p => new[] { p.A, p.B }).Distinct().ToList();
        var rows = new Dictionary<long, (string Text, string? Stem)>();
        foreach (var chunk in ids.Chunk(500))
        {
            var part = await db.Questions.AsNoTracking().Where(q => chunk.Contains(q.Id))
                .Select(q => new { q.Id, q.OriginalText, q.StemHash }).ToListAsync(cancellationToken);
            foreach (var r in part)
                rows[r.Id] = (r.OriginalText, r.StemHash);
        }

        var parent = ids.ToDictionary(i => i, i => i);
        long Find(long x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var best = new Dictionary<(long, long), double>();
        foreach (var p in pairs)
        {
            var (ta, sa) = rows[p.A];
            var (tb, sb) = rows[p.B];
            if (sa is not null && sa == sb)
                continue; // النص نفسه: فئة SameStem أو Exact
            var sim = NearDuplicates.Similarity(ta, tb);
            if (sim < NearThreshold)
                continue;
            best[(p.A, p.B)] = sim;
            parent[Find(p.A)] = Find(p.B);
        }

        var grouped = best.Keys.SelectMany(k => new[] { k.Item1, k.Item2 }).Distinct()
            .GroupBy(Find).Where(g => g.Count() > 1)
            .Select(g => (Members: g.Order().ToList(), Min: best.Where(kv => Find(kv.Key.Item1) == g.Key).Min(kv => kv.Value)))
            .OrderByDescending(g => g.Members.Count).ThenBy(g => g.Members[0])
            .ToList();

        var page = grouped.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).ToList();
        var memberIds = page.SelectMany(g => g.Members).ToList();
        var details = await db.Questions.AsNoTracking().Where(q => memberIds.Contains(q.Id))
            .Select(q => new
            {
                Q = q,
                File = db.SourceFiles.Where(f => f.Id == q.SourceFileId).Select(f => f.RelativePath).FirstOrDefault(),
                Upload = db.Uploads.Where(u => u.Id == q.UploadId).Select(u => u.ClientPath ?? u.OriginalName).FirstOrDefault()
            }).ToListAsync(cancellationToken);

        var groups = page.Select(g =>
        {
            var members = g.Members.Select(id => details.Single(d => d.Q.Id == id)).Select(m =>
            {
                var options = JsonSerializer.Deserialize<string[]>(m.Q.OriginalOptionsJson) ?? [];
                return new DuplicateMember(m.Q.Id, m.Q.OriginalText, options, m.Q.CorrectedIndex ?? m.Q.OriginalCorrectIndex,
                    m.Q.ReviewStatus.ToString(), m.File ?? ("رفع يدوي/" + m.Upload), m.Q.Location);
            }).ToList();
            return new DuplicateGroup("near-" + g.Members[0], members.Count, false, members, Math.Round(g.Min, 2));
        }).ToList();

        return (grouped.Count, groups);
    }

    /// <summary>يحسب البصمات للأسئلة التي لا بصمة لها (المستخرجة قبل إضافة الميزة). آمن للتكرار.</summary>
    public async Task<int> BackfillAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            var batch = await db.Questions.Where(q => q.FullHash == null).OrderBy(q => q.Id).Take(500).ToListAsync(cancellationToken);
            if (batch.Count == 0)
                return total;
            foreach (var q in batch)
            {
                var options = JsonSerializer.Deserialize<string[]>(q.OriginalOptionsJson) ?? [];
                q.StemHash = TextNormalizer.StemHash(q.OriginalText);
                q.FullHash = TextNormalizer.FullHash(q.OriginalText, options);
            }
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            total += batch.Count;
        }
    }

    public async Task<DuplicatePage> GetGroupsAsync(DuplicateKind kind, int skip, int take, CancellationToken cancellationToken)
    {
        var active = db.Questions.AsNoTracking().Where(q => q.ReviewStatus != QuestionReviewStatus.Superseded && q.FullHash != null);

        var exactKeys = active.GroupBy(q => q.FullHash!).Where(g => g.Count() > 1).Select(g => new { g.Key, Count = g.Count() });
        // النص نفسه بأكثر من مجموعة خيارات مختلفة.
        var stemKeys = active.GroupBy(q => q.StemHash!).Where(g => g.Select(q => q.FullHash).Distinct().Count() > 1)
            .Select(g => new { g.Key, Count = g.Count() });

        var exactCount = await exactKeys.CountAsync(cancellationToken);
        var stemCount = await stemKeys.CountAsync(cancellationToken);

        var keys = await (kind == DuplicateKind.Exact ? exactKeys : stemKeys)
            .OrderByDescending(g => g.Count).ThenBy(g => g.Key)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);
        var keyList = keys.Select(k => k.Key).ToList();

        var members = await active
            .Where(q => kind == DuplicateKind.Exact ? keyList.Contains(q.FullHash!) : keyList.Contains(q.StemHash!))
            .Select(q => new
            {
                Q = q,
                File = db.SourceFiles.Where(f => f.Id == q.SourceFileId).Select(f => f.RelativePath).FirstOrDefault(),
                Upload = db.Uploads.Where(u => u.Id == q.UploadId).Select(u => u.ClientPath ?? u.OriginalName).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var groups = keys.Select(k =>
        {
            var list = members
                .Where(m => (kind == DuplicateKind.Exact ? m.Q.FullHash : m.Q.StemHash) == k.Key)
                .OrderBy(m => m.Q.Id)
                .Select(m =>
                {
                    var options = JsonSerializer.Deserialize<string[]>(m.Q.OriginalOptionsJson) ?? [];
                    return new DuplicateMember(m.Q.Id, m.Q.OriginalText, options, m.Q.CorrectedIndex ?? m.Q.OriginalCorrectIndex,
                        m.Q.ReviewStatus.ToString(), m.File ?? ("رفع يدوي/" + m.Upload), m.Q.Location);
                })
                .ToList();
            return new DuplicateGroup(k.Key, k.Count, AnswersDiffer(list), list);
        }).ToList();

        return new DuplicatePage(exactCount, stemCount, kind == DuplicateKind.Exact ? exactCount : stemCount, groups);
    }

    /// <summary>
    /// نسخ متطابقة بإجابات مختلفة: تُقارن نصوص الإجابات (بعد التطبيع) لا مواقعها، لأن ترتيب الخيارات قد يختلف.
    /// هذه أخطر حالة: أحدها خاطئ حتمًا.
    /// </summary>
    private static bool AnswersDiffer(IReadOnlyList<DuplicateMember> members) =>
        members.Where(m => m.EffectiveCorrectIndex is { } i && i < m.Options.Count)
            .Select(m => TextNormalizer.Normalize(m.Options[m.EffectiveCorrectIndex!.Value]))
            .Distinct().Count() > 1;
}
