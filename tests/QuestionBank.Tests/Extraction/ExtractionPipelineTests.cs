using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestionBank.Core.Data;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Inventory;
using QuestionBank.Tests.Inventory;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace QuestionBank.Tests.Extraction;

public sealed class ExtractionPipelineTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();
    private readonly UploadOptions _uploadOptions = new() { MaxFileSizeMb = 1 };

    public void Dispose() => _kit.Dispose();

    private const string SampleTxt = "1. سؤال أول؟\nأ) س\n*ب) ص\n\n2. سؤال ثانٍ؟\nأ) ع\nب) ل\n";
    private const string SampleGift = "::G1::سؤال؟ {=نعم ~لا ~ربما}\n";

    private ExtractionService Extraction(QuestionBankDbContext db) => new(db, ReaderRegistry.Default(), _kit.Time);

    private UploadService Uploads(QuestionBankDbContext db)
    {
        _kit.Options.SourceRoot = _kit.SourceRoot;
        _kit.Options.OutputRoot = _kit.OutputRoot;
        return new UploadService(db, MsOptions.Create(_kit.Options), MsOptions.Create(_uploadOptions), Extraction(db), _kit.Time);
    }

    private QueueProcessor Processor(QuestionBankDbContext db, ISourceFileSystem? fs = null)
    {
        _kit.Options.SourceRoot = _kit.SourceRoot;
        _kit.Options.OutputRoot = _kit.OutputRoot;
        return new QueueProcessor(db, fs ?? new PhysicalSourceFileSystem(), MsOptions.Create(_kit.Options),
            MsOptions.Create(_uploadOptions), Extraction(db), ReaderRegistry.Default(), _kit.Time,
            NullLogger<QueueProcessor>.Instance);
    }

    private static MemoryStream Stream(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Upload_IsStoredInOutputByHash_AndExtracted()
    {
        await using var db = _kit.CreateDb();
        var result = await Uploads(db).SaveAsync(Stream(SampleTxt), "أسئلة.txt", "مجلدي/أسئلة.txt", "CORP\\u", CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.False(result.Duplicate);
        Assert.Equal("Completed", result.Upload!.Status);
        Assert.Equal(2, result.Upload.QuestionCount);
        Assert.Empty(Directory.EnumerateFiles(_kit.SourceRoot, "*", SearchOption.AllDirectories));
        var stored = Directory.EnumerateFiles(Path.Combine(_kit.OutputRoot, "uploads"), "*.txt", SearchOption.AllDirectories).Single();
        Assert.Matches("^[0-9A-F]{64}\\.txt$", Path.GetFileName(stored));
        Assert.Equal(SampleTxt, File.ReadAllText(stored));

        var questions = await db.Questions.AsNoTracking().OrderBy(q => q.OriginalOrder).ToListAsync();
        Assert.Equal(QuestionReviewStatus.AwaitingApproval, questions[0].ReviewStatus);
        Assert.Equal(QuestionReviewStatus.NeedsReview, questions[1].ReviewStatus);
        Assert.All(questions, q => Assert.Equal(result.Upload.Id, q.UploadId));
    }

    [Fact]
    public async Task SameContentUploadedTwice_IsDetectedAsDuplicate()
    {
        await using var db = _kit.CreateDb();
        var svc = Uploads(db);
        var first = await svc.SaveAsync(Stream(SampleTxt), "a.txt", null, "u", CancellationToken.None);
        var second = await svc.SaveAsync(Stream(SampleTxt), "اسم-مختلف.txt", null, "u2", CancellationToken.None);

        Assert.True(second.Duplicate);
        Assert.Equal(first.Upload!.Id, second.Upload!.Id);
        Assert.Equal(1, await db.Uploads.CountAsync());
        Assert.Equal(2, await db.Questions.CountAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_kit.OutputRoot, "uploads", ".tmp")));
    }

    [Theory]
    [InlineData("برنامج.exe", UploadErrorCodes.ExtensionNotAllowed)]
    [InlineData("ملف.zip", UploadErrorCodes.ExtensionNotAllowed)]
    [InlineData("بلا-امتداد", UploadErrorCodes.ExtensionNotAllowed)]
    public async Task DisallowedExtensions_AreRejected(string name, string code)
    {
        await using var db = _kit.CreateDb();
        var result = await Uploads(db).SaveAsync(Stream("x"), name, null, "u", CancellationToken.None);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(0, await db.Uploads.CountAsync());
    }

    [Fact]
    public async Task TooLargeAndEmptyFiles_AreRejected_AndNoTempFilesRemain()
    {
        await using var db = _kit.CreateDb();
        var big = await Uploads(db).SaveAsync(new MemoryStream(new byte[2 * 1024 * 1024]), "كبير.txt", null, "u", CancellationToken.None);
        var empty = await Uploads(db).SaveAsync(new MemoryStream(), "فارغ.txt", null, "u", CancellationToken.None);

        Assert.Equal(UploadErrorCodes.TooLarge, big.ErrorCode);
        Assert.Equal(UploadErrorCodes.Empty, empty.ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_kit.OutputRoot, "uploads"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TraversalInFileName_NeverReachesTheDisk()
    {
        await using var db = _kit.CreateDb();
        var result = await Uploads(db).SaveAsync(Stream(SampleGift), "../../../خبيث.gift", "..\\..\\x", "u", CancellationToken.None);

        Assert.Equal("خبيث.gift", result.Upload!.OriginalName);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_kit.OutputRoot)!, "خبيث.gift")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_kit.OutputRoot, "uploads"), "*.gift", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UploadWithoutReader_StaysPending()
    {
        await using var db = _kit.CreateDb();
        var result = await Uploads(db).SaveAsync(Stream("%PDF fake"), "نموذج.pdf", null, "u", CancellationToken.None);
        Assert.Equal("Pending", result.Upload!.Status);
        Assert.False(result.Upload.Supported);
    }

    [Fact]
    public async Task ReExtraction_ReplacesQuestions_WithSameStableKeys()
    {
        _kit.WriteFile("مجلد/ملف.txt", SampleTxt);
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        var file = await db.SourceFiles.SingleAsync();
        var bytes = Encoding.UTF8.GetBytes(SampleTxt);

        await Extraction(db).ExtractAsync(bytes, "txt", file.Id, null, CancellationToken.None);
        var keys1 = await db.Questions.Select(q => q.StableKey).OrderBy(k => k).ToListAsync();
        await Extraction(db).ExtractAsync(bytes, "txt", file.Id, null, CancellationToken.None);
        var keys2 = await db.Questions.Select(q => q.StableKey).OrderBy(k => k).ToListAsync();

        Assert.Equal(2, keys2.Count);
        Assert.Equal(keys1, keys2);
    }

    [Fact]
    public async Task IdenticalFilesInTwoFolders_KeepSeparateQuestionsAndSameContentHash()
    {
        _kit.WriteFile("أ/نسخة.txt", SampleTxt);
        _kit.WriteFile("ب/نسخة.txt", SampleTxt);
        await _kit.ScanAsync();
        await using (var db = _kit.CreateDb())
        {
            db.ProcessingQueue.Add(new ProcessingQueueItem { RelativePath = "", RequestedBy = "u", FileCount = 2 });
            await db.SaveChangesAsync();
        }

        await using (var db = _kit.CreateDb())
        {
            while (await Processor(db).ProcessNextBatchAsync(CancellationToken.None)) { }
        }

        await using var check = _kit.CreateDb();
        var questions = await check.Questions.ToListAsync();
        Assert.Equal(4, questions.Count);
        Assert.Equal(4, questions.Select(q => q.StableKey).Distinct().Count());
        Assert.Single(questions.Select(q => q.ContentHash).Distinct());
    }

    [Fact]
    public async Task QueueProcessor_ExtractsSupportedFiles_LeavesOthersPending_AndDoesNotTouchSource()
    {
        _kit.WriteFile("إداري/أ.txt", SampleTxt);
        _kit.WriteFile("إداري/ب.gift", SampleGift);
        _kit.WriteFile("إداري/ج.pdf", "fake");
        _kit.WriteFile("محاسبة/د.txt", SampleTxt);
        await _kit.ScanAsync();
        var before = _kit.SnapshotSource();
        await using (var db = _kit.CreateDb())
        {
            await new FolderService(db, _kit.Time).EnqueueAsync("إداري", "CORP\\m", CancellationToken.None);
        }

        await using (var db = _kit.CreateDb())
        {
            var processor = Processor(db);
            while (await processor.ProcessNextBatchAsync(CancellationToken.None)) { }
        }

        Assert.Equal(before, _kit.SnapshotSource());
        var files = _kit.Files().ToDictionary(f => f.RelativePath);
        Assert.Equal(FileStatus.Completed, files["إداري/أ.txt"].Status);
        Assert.Equal(FileStatus.Completed, files["إداري/ب.gift"].Status);
        Assert.Equal(FileStatus.Pending, files["إداري/ج.pdf"].Status);
        Assert.Equal(FileStatus.Pending, files["محاسبة/د.txt"].Status); // خارج المجلد المطلوب
        Assert.NotNull(files["إداري/أ.txt"].ContentHash);
        Assert.NotNull(files["إداري/ب.gift"].ContentHash);
        await using var check = _kit.CreateDb();
        Assert.Equal(3, await check.Questions.CountAsync());
        Assert.Equal(QueueItemStatus.Completed, (await check.ProcessingQueue.SingleAsync()).Status);
    }

    [Fact]
    public async Task QueueProcessor_RecordsUnreadableFiles_AndContinues()
    {
        _kit.WriteFile("مجلد/سليم.txt", SampleTxt);
        _kit.WriteFile("مجلد/مقفل.txt", SampleTxt);
        await _kit.ScanAsync();
        await using (var db = _kit.CreateDb())
        {
            await new FolderService(db, _kit.Time).EnqueueAsync("مجلد", "u", CancellationToken.None);
        }
        var fs = new FaultInjectingFileSystem(_kit.SourceRoot)
        {
            ReadFault = rel => rel.EndsWith("مقفل.txt") ? new UnauthorizedAccessException() : null
        };

        await using (var db = _kit.CreateDb())
        {
            while (await Processor(db, fs).ProcessNextBatchAsync(CancellationToken.None)) { }
        }

        var files = _kit.Files().ToDictionary(f => f.RelativePath);
        Assert.Equal(FileStatus.Completed, files["مجلد/سليم.txt"].Status);
        Assert.Equal(FileStatus.Failed, files["مجلد/مقفل.txt"].Status);
        Assert.Equal(InventoryErrorCodes.AccessDenied, files["مجلد/مقفل.txt"].LastErrorCode);
    }

    [Fact]
    public async Task QueueProcessor_PausesWhenSourceRootIsUnavailable()
    {
        _kit.WriteFile("مجلد/ملف.txt", SampleTxt);
        await _kit.ScanAsync();
        await using var db = _kit.CreateDb();
        await new FolderService(db, _kit.Time).EnqueueAsync("مجلد", "u", CancellationToken.None);
        var fs = new FaultInjectingFileSystem(_kit.SourceRoot) { RootAvailable = false };

        await Assert.ThrowsAsync<SourceUnavailableException>(() => Processor(db, fs).ProcessNextBatchAsync(CancellationToken.None));
        Assert.Equal(FileStatus.Pending, _kit.Files().Single().Status);
    }
}
