using System.Text;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Quality;
using QuestionBank.Tests.Inventory;

namespace QuestionBank.Tests.Quality;

public sealed class NearDuplicateTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private const string A = "ما الخطوة الأولى التي يجب على الموظف اتخاذها عند تعارض مهمتين لهما موعد التسليم نفسه؟";
    private const string ANear = "ما الخطوة الأولى التي يجب على الموظف اتخاذها عند تعارض مهمتين لهما موعد تسليم واحد؟";
    private const string Different = "أي مما يلي يعد من أساليب إدارة الوقت الفعالة في بيئة العمل الحكومي؟";

    [Fact]
    public void Similarity_IsHighForRewordings_LowForDifferentQuestions()
    {
        Assert.True(NearDuplicates.Similarity(A, ANear) >= DuplicateService.NearThreshold);
        Assert.True(NearDuplicates.Similarity(A, Different) < 0.3);
        Assert.Equal(1.0, NearDuplicates.Similarity(A, A));
    }

    [Fact]
    public void BandKeys_AreDeterministic_AndShareBandsForNearTexts()
    {
        var k1 = NearDuplicates.BandKeys(A);
        Assert.Equal(NearDuplicates.Bands, k1.Count);
        Assert.Equal(k1, NearDuplicates.BandKeys(A));
        Assert.Contains(NearDuplicates.BandKeys(ANear), k1.Contains);
        Assert.Empty(NearDuplicates.BandKeys("قصير؟"));
    }

    private async Task SeedAsync(params (string Path, string Text)[] questions)
    {
        foreach (var (path, text) in questions)
            _kit.WriteFile(path, $"1. {text}\nأ) س\n*ب) ص\n");
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        var extraction = new ExtractionService(db, ReaderRegistry.Default(), _kit.Time);
        foreach (var f in await db.SourceFiles.ToListAsync())
        {
            var text = questions.Single(q => q.Path == f.RelativePath).Text;
            await extraction.ExtractAsync(Encoding.UTF8.GetBytes($"1. {text}\nأ) س\n*ب) ص\n"), "txt", f.Id, null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task NearGroups_FindRewordings_AndExcludeExactAndUnrelated()
    {
        await SeedAsync(("1.txt", A), ("2.txt", ANear), ("3.txt", Different), ("4.txt", A));

        await using var db = _kit.CreateDb();
        var (count, groups) = await new DuplicateService(db).GetNearGroupsAsync(0, 50, CancellationToken.None);

        // 1 و4 متطابقان (فئة Exact)، وكلاهما شبيه بـ 2 فيكوّنون مجموعة شبه تكرار واحدة. 3 مختلف.
        Assert.Equal(1, count);
        var g = Assert.Single(groups);
        Assert.Equal(["1.txt", "2.txt", "4.txt"], g.Members.Select(m => m.Source).Order(StringComparer.Ordinal).ToArray());
        Assert.True(g.Similarity >= DuplicateService.NearThreshold);
    }

    [Fact]
    public async Task OnlyExactCopies_ProduceNoNearGroups()
    {
        await SeedAsync(("1.txt", A), ("2.txt", A));
        await using var db = _kit.CreateDb();
        Assert.Equal(0, (await new DuplicateService(db).GetNearGroupsAsync(0, 50, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task BandsFollowQuestions_AndBackfillRestoresMissingOnes()
    {
        await SeedAsync(("1.txt", A), ("2.txt", ANear));
        await using (var db = _kit.CreateDb())
        {
            Assert.Equal(2 * NearDuplicates.Bands, await db.QuestionBands.CountAsync());
            await db.QuestionBands.ExecuteDeleteAsync();
            Assert.Equal(0, (await new DuplicateService(db).GetNearGroupsAsync(0, 50, CancellationToken.None)).Count);
            Assert.Equal(2, await new DuplicateService(db).BackfillBandsAsync(CancellationToken.None));
            Assert.Equal(1, (await new DuplicateService(db).GetNearGroupsAsync(0, 50, CancellationToken.None)).Count);
        }
    }
}
