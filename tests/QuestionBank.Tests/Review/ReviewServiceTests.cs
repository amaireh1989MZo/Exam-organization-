using System.Text;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Review;
using QuestionBank.Tests.Inventory;

namespace QuestionBank.Tests.Review;

public sealed class ReviewServiceTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    // سؤال 1: إجابته ب (ثقة عالية). سؤال 2: بلا إجابة (يحتاج مراجعة).
    private const string Txt = "1. سؤال أول؟\nأ) س\n*ب) ص\nج) ع\n\n2. سؤال ثانٍ؟\nأ) ل\nب) م\n";

    private async Task<(long FileId, long Q1, long Q2)> SeedAsync(string content = Txt)
    {
        _kit.WriteFile("مجلد/ملف.txt", content);
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        var file = await db.SourceFiles.SingleAsync();
        await new ExtractionService(db, ReaderRegistry.Default(), _kit.Time)
            .ExtractAsync(Encoding.UTF8.GetBytes(content), "txt", file.Id, null, CancellationToken.None);
        var qs = await db.Questions.OrderBy(q => q.OriginalOrder).Select(q => q.Id).ToListAsync();
        return (file.Id, qs[0], qs[1]);
    }

    private async Task<T> WithService<T>(Func<ReviewService, Task<T>> action)
    {
        await using var db = _kit.CreateDb();
        return await action(new ReviewService(db, _kit.Time));
    }

    private Question Load(long id)
    {
        using var db = _kit.CreateDb();
        return db.Questions.AsNoTracking().Single(q => q.Id == id);
    }

    [Fact]
    public async Task List_ShowsLowConfidenceFirst_WithCountsAndSource()
    {
        var (_, q1, q2) = await SeedAsync();

        var page = await WithService(s => s.ListAsync(null, 0, 50, CancellationToken.None));

        Assert.Equal(2, page.Total);
        Assert.Equal(q2, page.Items[0].Id);
        Assert.Equal(q1, page.Items[1].Id);
        Assert.Equal("مجلد/ملف.txt", page.Items[0].Source);
        Assert.Equal(1, page.Counts["AwaitingApproval"]);
        Assert.Equal(1, page.Counts["NeedsReview"]);
    }

    [Fact]
    public async Task SetAnswer_IsACorrectionLayer_OriginalNeverChanges_AndIsAudited()
    {
        var (_, q1, _) = await SeedAsync();

        Assert.Equal(ReviewOutcome.Ok, await WithService(s => s.SetAnswerAsync(q1, 2, "CORP\\reviewer", CancellationToken.None)));

        var q = Load(q1);
        Assert.Equal(1, q.OriginalCorrectIndex);
        Assert.Equal(2, q.CorrectedIndex);
        Assert.Equal("CORP\\reviewer", q.ReviewedBy);
        var history = await WithService(s => s.HistoryAsync(q1, CancellationToken.None));
        var entry = Assert.Single(history);
        Assert.Equal((AuditActions.SetAnswer, "1", "2", "CORP\\reviewer"), (entry.Action, entry.OldValue, entry.NewValue, entry.User));
        Assert.DoesNotContain("سؤال", entry.OldValue + entry.NewValue);
    }

    [Fact]
    public async Task SettingOriginalAnswerOrNull_RemovesTheCorrection()
    {
        var (_, q1, _) = await SeedAsync();
        await WithService(s => s.SetAnswerAsync(q1, 0, "u", CancellationToken.None));

        await WithService(s => s.SetAnswerAsync(q1, 1, "u", CancellationToken.None));
        Assert.Null(Load(q1).CorrectedIndex);

        await WithService(s => s.SetAnswerAsync(q1, 0, "u", CancellationToken.None));
        await WithService(s => s.SetAnswerAsync(q1, null, "u", CancellationToken.None));
        Assert.Null(Load(q1).CorrectedIndex);
        Assert.Equal(AuditActions.RevertAnswer, (await WithService(s => s.HistoryAsync(q1, CancellationToken.None)))[^1].Action);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task InvalidOptionIndex_IsRejected(int index)
    {
        var (_, q1, _) = await SeedAsync();
        Assert.Equal(ReviewOutcome.InvalidOption, await WithService(s => s.SetAnswerAsync(q1, index, "u", CancellationToken.None)));
        Assert.Empty(await WithService(s => s.HistoryAsync(q1, CancellationToken.None)));
    }

    [Fact]
    public async Task Approve_RequiresAnAnswer_ThenSucceedsAfterCorrection()
    {
        var (_, _, q2) = await SeedAsync();

        Assert.Equal(ReviewOutcome.NoAnswer, await WithService(s => s.ApproveAsync(q2, "u", CancellationToken.None)));
        await WithService(s => s.SetAnswerAsync(q2, 1, "u", CancellationToken.None));
        Assert.Equal(ReviewOutcome.Ok, await WithService(s => s.ApproveAsync(q2, "u", CancellationToken.None)));

        Assert.Equal(QuestionReviewStatus.Approved, Load(q2).ReviewStatus);
    }

    [Fact]
    public async Task ApprovedQuestion_CannotBeEditedUntilReopened()
    {
        var (_, q1, _) = await SeedAsync();
        await WithService(s => s.ApproveAsync(q1, "u", CancellationToken.None));

        Assert.Equal(ReviewOutcome.NotAllowedInStatus, await WithService(s => s.SetAnswerAsync(q1, 0, "u", CancellationToken.None)));
        await WithService(s => s.ReopenAsync(q1, "u", CancellationToken.None));
        Assert.Equal(QuestionReviewStatus.AwaitingApproval, Load(q1).ReviewStatus);
        Assert.Equal(ReviewOutcome.Ok, await WithService(s => s.SetAnswerAsync(q1, 0, "u", CancellationToken.None)));
    }

    [Fact]
    public async Task Flag_AndReopen_AreAudited_RepeatedActionsAreNotDuplicated()
    {
        var (_, _, q2) = await SeedAsync();

        await WithService(s => s.FlagAsync(q2, "u", CancellationToken.None));
        await WithService(s => s.FlagAsync(q2, "u", CancellationToken.None));
        await WithService(s => s.ReopenAsync(q2, "u", CancellationToken.None));

        Assert.Equal(QuestionReviewStatus.NeedsReview, Load(q2).ReviewStatus);
        Assert.Equal([AuditActions.Flag, AuditActions.Reopen],
            (await WithService(s => s.HistoryAsync(q2, CancellationToken.None))).Select(a => a.Action).ToArray());
    }

    [Fact]
    public async Task ReExtractingSameContent_KeepsReviewWork()
    {
        var (fileId, q1, _) = await SeedAsync();
        await WithService(s => s.SetAnswerAsync(q1, 2, "u", CancellationToken.None));
        await WithService(s => s.ApproveAsync(q1, "u", CancellationToken.None));

        await using (var db = _kit.CreateDb())
        {
            await new ExtractionService(db, ReaderRegistry.Default(), _kit.Time)
                .ExtractAsync(Encoding.UTF8.GetBytes(Txt), "txt", fileId, null, CancellationToken.None);
        }

        var q = Load(q1);
        Assert.Equal(QuestionReviewStatus.Approved, q.ReviewStatus);
        Assert.Equal(2, q.CorrectedIndex);
    }

    [Fact]
    public async Task ChangedFile_SupersedesReviewedQuestions_DeletesUnreviewed_AndInsertsNew()
    {
        var (fileId, q1, q2) = await SeedAsync();
        await WithService(s => s.ApproveAsync(q1, "u", CancellationToken.None));
        const string changed = "1. سؤال معدّل؟\nأ) س\n*ب) ص\n";

        await using (var db = _kit.CreateDb())
        {
            await new ExtractionService(db, ReaderRegistry.Default(), _kit.Time)
                .ExtractAsync(Encoding.UTF8.GetBytes(changed), "txt", fileId, null, CancellationToken.None);
        }

        await using var check = _kit.CreateDb();
        var all = await check.Questions.AsNoTracking().ToListAsync();
        Assert.Equal(QuestionReviewStatus.Superseded, all.Single(q => q.Id == q1).ReviewStatus);
        Assert.DoesNotContain(all, q => q.Id == q2);
        Assert.Single(all, q => q.OriginalText == "سؤال معدّل؟");
        Assert.Contains(await check.AuditLog.ToListAsync(), a => a.EntityId == q1 && a.Action == "Supersede");

        // العودة إلى المحتوى الأصلي لا تصطدم بمعرّف السؤال المستبدَل.
        await new ExtractionService(check, ReaderRegistry.Default(), _kit.Time)
            .ExtractAsync(Encoding.UTF8.GetBytes(Txt), "txt", fileId, null, CancellationToken.None);
        Assert.Equal(all.Count + 2 - 1, await check.Questions.CountAsync());
    }
}
