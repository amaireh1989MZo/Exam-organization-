using System.Text;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Quality;
using QuestionBank.Tests.Inventory;

namespace QuestionBank.Tests.Quality;

public sealed class DuplicateServiceTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    [Theory]
    [InlineData("ما عاصمةُ الأردنِّ؟", "ما عاصمه الاردن")]
    [InlineData("إلى  أين؟", "الي اين")]
    [InlineData("مَدرسـة", "مدرسه")]
    [InlineData("السؤال ١٢", "السوال 12")]
    [InlineData("Hello, World!", "hello world")]
    public void Normalize_RemovesCosmeticDifferencesOnly(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void FullHash_IgnoresOptionOrder_ButNotOptionContent()
    {
        var a = TextNormalizer.FullHash("سؤال؟", ["أ", "ب", "ج"]);
        Assert.Equal(a, TextNormalizer.FullHash("سُؤال", ["ج", "أ", "ب"]));
        Assert.NotEqual(a, TextNormalizer.FullHash("سؤال؟", ["أ", "ب", "د"]));
    }

    private async Task ExtractAsync(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
            _kit.WriteFile(path, content);
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        var extraction = new ExtractionService(db, ReaderRegistry.Default(), _kit.Time);
        foreach (var f in await db.SourceFiles.ToListAsync())
        {
            var content = files.Single(x => x.Path == f.RelativePath).Content;
            await extraction.ExtractAsync(Encoding.UTF8.GetBytes(content), "txt", f.Id, null, CancellationToken.None);
        }
    }

    private async Task<DuplicatePage> GroupsAsync(DuplicateKind kind)
    {
        await using var db = _kit.CreateDb();
        return await new DuplicateService(db).GetGroupsAsync(kind, 0, 50, CancellationToken.None);
    }

    [Fact]
    public async Task ExactDuplicates_AcrossFiles_AreGrouped_EvenWithDiacriticsAndReorderedOptions()
    {
        await ExtractAsync(
            ("2022/أ.txt", "1. ما عاصمة الأردن؟\nأ) إربد\n*ب) عمّان\nج) الزرقاء\n\n2. سؤال فريد؟\n*أ) س\nب) ص"),
            ("2023/ب.txt", "1. ما عاصمةُ الأردن؟\nأ) عمان\nب) الزرقاء\n*ج) إربد"));

        var page = await GroupsAsync(DuplicateKind.Exact);

        var group = Assert.Single(page.Groups);
        Assert.Equal(2, group.Count);
        Assert.Equal(["2022/أ.txt", "2023/ب.txt"], group.Members.Select(m => m.Source).Order(StringComparer.Ordinal).ToArray());
        // النسختان متطابقتان لكن إحداهما تعطي "عمّان" والأخرى "إربد": أخطر حالة.
        Assert.True(group.AnswersDiffer);
        Assert.Equal(1, page.ExactGroups);
    }

    [Fact]
    public async Task SameAnswerText_InDifferentPositions_IsNotReportedAsDiffering()
    {
        await ExtractAsync(
            ("أ.txt", "1. سؤال؟\nأ) س\n*ب) ص"),
            ("ب.txt", "1. سؤال؟\n*أ) ص\nب) س"));

        Assert.False(Assert.Single((await GroupsAsync(DuplicateKind.Exact)).Groups).AnswersDiffer);
    }

    [Fact]
    public async Task SameStemWithDifferentOptions_IsASeparateCategory()
    {
        await ExtractAsync(
            ("أ.txt", "1. الخطوة الأولى في حل المشكلات هي:\n*أ) تحديد المشكلة\nب) اقتراح الحلول"),
            ("ب.txt", "1. الخطوة الأولى في حل المشكلات هي:\n*أ) تحديد المشكلة\nب) تنفيذ الحل\nج) التقييم"));

        Assert.Empty((await GroupsAsync(DuplicateKind.Exact)).Groups);
        var stem = await GroupsAsync(DuplicateKind.SameStem);
        Assert.Equal(2, Assert.Single(stem.Groups).Count);
    }

    [Fact]
    public async Task NothingIsDeletedOrChanged()
    {
        await ExtractAsync(("أ.txt", "1. سؤال؟\nأ) س\n*ب) ص"), ("ب.txt", "1. سؤال؟\nأ) س\n*ب) ص"));
        await using var db = _kit.CreateDb();
        var before = await db.Questions.AsNoTracking().Select(q => new { q.Id, q.OriginalText, q.ReviewStatus }).ToListAsync();

        await GroupsAsync(DuplicateKind.Exact);

        var after = await db.Questions.AsNoTracking().Select(q => new { q.Id, q.OriginalText, q.ReviewStatus }).ToListAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Backfill_ComputesMissingHashes()
    {
        await ExtractAsync(("أ.txt", "1. سؤال؟\nأ) س\n*ب) ص"), ("ب.txt", "1. سؤال؟\nأ) س\n*ب) ص"));
        await using (var db = _kit.CreateDb())
            await db.Questions.ExecuteUpdateAsync(s => s.SetProperty(q => q.FullHash, (string?)null).SetProperty(q => q.StemHash, (string?)null));
        Assert.Empty((await GroupsAsync(DuplicateKind.Exact)).Groups);

        await using (var db = _kit.CreateDb())
            Assert.Equal(2, await new DuplicateService(db).BackfillAsync(CancellationToken.None));

        Assert.Single((await GroupsAsync(DuplicateKind.Exact)).Groups);
    }
}
