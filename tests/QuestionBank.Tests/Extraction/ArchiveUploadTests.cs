using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Extraction;
using QuestionBank.Tests.Inventory;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace QuestionBank.Tests.Extraction;

public sealed class ArchiveUploadTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();
    private readonly UploadOptions _options = new() { MaxFileSizeMb = 5, MaxZipEntries = 10, MaxZipTotalMb = 5, MaxCompressionRatio = 50 };

    public void Dispose() => _kit.Dispose();

    private static MemoryStream Zip(params (string Name, byte[] Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private async Task<ArchiveResult> UploadAsync(Stream zip, string name = "حزمة.zip")
    {
        _kit.Options.SourceRoot = _kit.SourceRoot;
        _kit.Options.OutputRoot = _kit.OutputRoot;
        await using var db = _kit.CreateDb();
        var service = new UploadService(db, MsOptions.Create(_kit.Options), MsOptions.Create(_options),
            new ExtractionService(db, ReaderRegistry.Default(), _kit.Time), _kit.Time);
        return await service.SaveArchiveAsync(zip, name, "CORP\\u", CancellationToken.None);
    }

    [Fact]
    public async Task Archive_ExtractsAllowedFiles_SkipsOthers_AndKeepsFolderPathsForDisplay()
    {
        var result = await UploadAsync(Zip(
            ("2023/أسئلة.txt", Utf8("1. سؤال؟\nأ) س\n*ب) ص")),
            ("2023/فرعي/بنك.gift", Utf8("سؤال؟ {=نعم ~لا}")),
            ("برنامج.exe", [1, 2, 3]),
            ("داخلي.zip", [1, 2, 3])));

        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(["حزمة.zip/2023/أسئلة.txt", "حزمة.zip/2023/فرعي/بنك.gift"],
            result.Items.Select(i => i.Upload!.ClientPath!).Order(StringComparer.Ordinal).ToArray());
        Assert.All(result.Items, i => Assert.Equal(1, i.Upload!.QuestionCount));
        Assert.Equal([UploadErrorCodes.ExtensionNotAllowed, UploadErrorCodes.NestedArchive],
            result.Skipped.Select(s => s.Code).Order().ToArray());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_kit.OutputRoot, "uploads", ".tmp")));
    }

    [Fact]
    public async Task TraversalEntryNames_NeverWriteOutsideUploads()
    {
        var result = await UploadAsync(Zip(("../../../خبيث.txt", Utf8("1. س؟\n*أ) ا\nب) ب")), ("/etc/مطلق.txt", Utf8("نص"))));

        Assert.Equal(2, result.Items.Count);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_kit.OutputRoot)!, "خبيث.txt")));
        Assert.All(Directory.EnumerateFiles(_kit.OutputRoot, "*", SearchOption.AllDirectories),
            f => Assert.StartsWith(Path.Combine(_kit.OutputRoot, "uploads"), f));
        Assert.Equal("خبيث.txt", result.Items[0].Upload!.OriginalName);
    }

    [Fact]
    public async Task HighCompressionRatio_IsRejectedAsZipBomb()
    {
        var result = await UploadAsync(Zip(("أصفار.txt", new byte[3 * 1024 * 1024])));

        Assert.Empty(result.Items);
        Assert.Equal(UploadErrorCodes.SuspiciousCompression, Assert.Single(result.Skipped).Code);
        await using var db = _kit.CreateDb();
        Assert.Equal(0, await db.Uploads.CountAsync());
    }

    [Fact]
    public async Task TooManyEntries_OrTooLargeTotal_RejectsWholeArchive()
    {
        var many = Enumerable.Range(0, 11).Select(i => ($"{i}.txt", Utf8("نص " + i))).ToArray();
        Assert.Equal(UploadErrorCodes.TooManyEntries, (await UploadAsync(Zip(many))).ErrorCode);

        var random = new Random(1);
        byte[] Noise(int n) { var b = new byte[n]; random.NextBytes(b); return b; }
        var big = Zip(("أ.txt", Noise(3 * 1024 * 1024)), ("ب.txt", Noise(3 * 1024 * 1024)));
        _options.MaxFileSizeMb = 20;
        Assert.Equal(UploadErrorCodes.ArchiveTooLarge, (await UploadAsync(big)).ErrorCode);
    }

    [Fact]
    public async Task CorruptArchive_IsRejected()
    {
        var result = await UploadAsync(new MemoryStream(Utf8("ليس ملف zip")));
        Assert.Equal(UploadErrorCodes.InvalidArchive, result.ErrorCode);
    }

    [Fact]
    public void LimitedReadStream_ThrowsBeyondLimit_RegardlessOfDeclaredSize()
    {
        var s = new LimitedReadStream(new MemoryStream(new byte[100]), 50);
        var buffer = new byte[40];
        s.ReadExactly(buffer);
        Assert.Throws<InvalidDataException>(() => s.ReadExactly(buffer));
    }
}
