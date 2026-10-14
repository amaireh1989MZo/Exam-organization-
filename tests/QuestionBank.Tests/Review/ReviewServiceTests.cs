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

public sealed class TextCorrectionTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private const string Txt = "1. ما عاصمة الاردن؟؟\nأ) إربد\n*ب) عمـان\nج) الزرقاء\n";

    private async Task<long> SeedAsync()
    {
        _kit.WriteFile("أ.txt", Txt);
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        var file = await db.SourceFiles.SingleAsync();
        await new ExtractionService(db, ReaderRegistry.Default(), _kit.Time).ExtractAsync(Encoding.UTF8.GetBytes(Txt), "txt", file.Id, null, CancellationToken.None);
        return await db.Questions.Select(q => q.Id).SingleAsync();
    }

    [Fact]
    public async Task TextCorrection_KeepsOriginal_IsShownAsEffective_AndAuditsFieldNamesOnly()
    {
        var id = await SeedAsync();
        await using var db = _kit.CreateDb();
        var svc = new ReviewService(db, _kit.Time);

        Assert.Equal(ReviewOutcome.Ok, await svc.SetTextAsync(id, "ما عاصمة الأردن؟", ["إربد", "عمّان", "الزرقاء"], "CORP\\r", CancellationToken.None));

        var q = await db.Questions.AsNoTracking().SingleAsync();
        Assert.Equal("ما عاصمة الاردن؟؟", q.OriginalText);
        Assert.Equal("ما عاصمة الأردن؟", q.CorrectedText);
        var item = (await svc.ListAsync(null, 0, 10, CancellationToken.None)).Items.Single();
        Assert.True(item.TextCorrected);
        Assert.Equal("ما عاصمة الأردن؟", item.Text);
        Assert.Equal("عمّان", item.Options[1]);
        Assert.Equal("ما عاصمة الاردن؟؟", item.OriginalText);
        var audit = (await svc.HistoryAsync(id, CancellationToken.None)).Single();
        Assert.Equal((AuditActions.SetText, "text,option2"), (audit.Action, audit.NewValue));
    }

    [Fact]
    public async Task InvalidCorrections_AreRejected()
    {
        var id = await SeedAsync();
        await using var db = _kit.CreateDb();
        var svc = new ReviewService(db, _kit.Time);

        Assert.Equal(ReviewOutcome.InvalidText, await svc.SetTextAsync(id, "  ", null, "u", CancellationToken.None));
        Assert.Equal(ReviewOutcome.InvalidText, await svc.SetTextAsync(id, null, ["أ", "ب"], "u", CancellationToken.None));
        Assert.Equal(ReviewOutcome.InvalidText, await svc.SetTextAsync(id, null, ["أ", "", "ج"], "u", CancellationToken.None));
        await svc.ApproveAsync(id, "u", CancellationToken.None);
        Assert.Equal(ReviewOutcome.NotAllowedInStatus, await svc.SetTextAsync(id, "نص", null, "u", CancellationToken.None));
    }

    [Fact]
    public async Task RevertingOrSettingTheOriginal_RemovesTheCorrection()
    {
        var id = await SeedAsync();
        await using var db = _kit.CreateDb();
        var svc = new ReviewService(db, _kit.Time);
        await svc.SetTextAsync(id, "نص معدّل؟", null, "u", CancellationToken.None);

        await svc.SetTextAsync(id, "ما عاصمة الاردن؟؟", null, "u", CancellationToken.None);
        Assert.Null((await db.Questions.AsNoTracking().SingleAsync()).CorrectedText);

        await svc.SetTextAsync(id, "نص معدّل؟", null, "u", CancellationToken.None);
        await svc.SetTextAsync(id, null, null, "u", CancellationToken.None);
        var q = await db.Questions.AsNoTracking().SingleAsync();
        Assert.Null(q.CorrectedText);
        Assert.Null(q.CorrectedOptionsJson);
        Assert.Equal(AuditActions.RevertText, (await svc.HistoryAsync(id, CancellationToken.None)).Last().Action);
    }

    [Fact]
    public async Task Export_UsesCorrectedText()
    {
        var id = await SeedAsync();
        await using (var db = _kit.CreateDb())
        {
            var svc = new ReviewService(db, _kit.Time);
            await svc.SetTextAsync(id, "ما عاصمة الأردن؟", ["إربد", "عمّان", "الزرقاء"], "u", CancellationToken.None);
            await svc.ApproveAsync(id, "u", CancellationToken.None);
        }
        _kit.Options.SourceRoot = _kit.SourceRoot;
        _kit.Options.OutputRoot = _kit.OutputRoot;
        await using var db2 = _kit.CreateDb();
        var result = await new QuestionBank.Core.Export.ExportService(db2, Microsoft.Extensions.Options.Options.Create(_kit.Options), _kit.Time)
            .ExportAsync(new QuestionBank.Core.Export.ExportRequest(QuestionBank.Core.Export.ExportFormat.Gift, [id], false, false), "u", CancellationToken.None);

        var parsed = new GiftReader().Read(File.ReadAllBytes(Path.Combine(_kit.OutputRoot, "exports", result.FileName!))).Questions.Single();
        Assert.Equal("ما عاصمة الأردن؟", parsed.Text);
        Assert.Equal("عمّان", parsed.Options[1]);
        Assert.Equal(1, parsed.CorrectIndex);
    }
}

public sealed class AssignmentTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private async Task SeedAsync()
    {
        var txt = string.Join("\n\n", Enumerable.Range(1, 4).Select(i => $"{i}. سؤال {i}؟\nأ) س\n" + (i % 2 == 0 ? "*" : "") + "ب) ص"));
        _kit.WriteFile("إداري/أ.txt", txt);
        _kit.WriteFile("محاسبة/ب.txt", txt);
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        var extraction = new ExtractionService(db, ReaderRegistry.Default(), _kit.Time);
        foreach (var f in await db.SourceFiles.ToListAsync())
            await extraction.ExtractAsync(Encoding.UTF8.GetBytes(txt), "txt", f.Id, null, CancellationToken.None);
    }

    [Fact]
    public async Task Assign_TakesUnassignedOpenQuestions_FromFolder_NeedsReviewFirst()
    {
        await SeedAsync();
        await using var db = _kit.CreateDb();
        var svc = new ReviewService(db, _kit.Time);

        Assert.Equal(3, await svc.AssignAsync(new AssignmentRequest("CORP\\ahmad", "إداري", 3), "CORP\\m", CancellationToken.None));
        Assert.Equal(1, await svc.AssignAsync(new AssignmentRequest("CORP\\sara", "إداري/", 10), "CORP\\m", CancellationToken.None));

        var ahmad = await db.Questions.AsNoTracking().Where(q => q.AssignedTo == "CORP\\ahmad").ToListAsync();
        Assert.Equal(2, ahmad.Count(q => q.ReviewStatus == QuestionReviewStatus.NeedsReview)); // تحتاج مراجعة أولًا
        Assert.All(await db.Questions.AsNoTracking().Where(q => q.AssignedTo != null).ToListAsync(),
            q => Assert.NotNull(q.SourceFileId));
        Assert.Equal(4, await db.Questions.CountAsync(q => q.AssignedTo == null)); // أسئلة المحاسبة لم تُمس
        Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "Assign" && a.NewValue == "CORP\\ahmad:3");
    }

    [Fact]
    public async Task MineFilter_AndProgress_AndUnassignOnlyOpen()
    {
        await SeedAsync();
        await using var db = _kit.CreateDb();
        var svc = new ReviewService(db, _kit.Time);
        // أربعة بلا إجابة تُسند أولًا، ثم واحد بإجابة معلَّمة
        await svc.AssignAsync(new AssignmentRequest("CORP\\ahmad", null, 5), "m", CancellationToken.None);
        var mine = await svc.ListAsync(null, 0, 50, CancellationToken.None, assignedTo: "CORP\\ahmad");
        Assert.Equal(5, mine.Total);

        var approvable = mine.Items.First(i => i.EffectiveCorrectIndex is not null);
        await svc.ApproveAsync(approvable.Id, "CORP\\ahmad", CancellationToken.None);
        var progress = Assert.Single(await svc.ProgressAsync(CancellationToken.None));
        Assert.Equal(("CORP\\ahmad", 5, 4, 1), (progress.Reviewer, progress.Assigned, progress.Open, progress.Approved));

        Assert.Equal(4, await svc.UnassignOpenAsync("CORP\\ahmad", "m", CancellationToken.None));
        Assert.Equal(1, await db.Questions.CountAsync(q => q.AssignedTo == "CORP\\ahmad")); // المعتمد يبقى باسمه
    }

    [Fact]
    public async Task InvalidRequests_AssignNothing()
    {
        await SeedAsync();
        await using var db = _kit.CreateDb();
        var svc = new ReviewService(db, _kit.Time);
        Assert.Equal(0, await svc.AssignAsync(new AssignmentRequest("  ", null, 5), "m", CancellationToken.None));
        Assert.Equal(0, await svc.AssignAsync(new AssignmentRequest("CORP\\x", null, 0), "m", CancellationToken.None));
        Assert.Equal(0, await svc.AssignAsync(new AssignmentRequest("CORP\\x", "غير-موجود", 5), "m", CancellationToken.None));
    }
}
