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
    SameStem
}

public sealed record DuplicateMember(long Id, string Text, IReadOnlyList<string> Options, int? EffectiveCorrectIndex, string ReviewStatus, string Source, string Location);

public sealed record DuplicateGroup(string Key, int Count, bool AnswersDiffer, IReadOnlyList<DuplicateMember> Members);

public sealed record DuplicatePage(int ExactGroups, int SameStemGroups, int Total, IReadOnlyList<DuplicateGroup> Groups);

/// <summary>
/// كشف التكرار الحرفي، والنص نفسه بخيارات مختلفة. لا يحذف ولا يعدّل أي سؤال؛ يعرض المجموعات للمراجع واللجنة.
/// الأسئلة المُستبدَلة مستبعدة.
/// </summary>
public sealed class DuplicateService(QuestionBankDbContext db)
{
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
