using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using QuestionBank.Core.Data;
using QuestionBank.Core.Inventory;

namespace QuestionBank.Tests.Inventory;

/// <summary>
/// بيئة اختبار معزولة: مجلد مصدر اصطناعي، ومجلد نتائج منفصل، وقاعدة SQLite في ملف مؤقت
/// (ملف وليس ذاكرة، حتى نختبر الاستئناف بسياق قاعدة جديد كما يحدث بعد إعادة تشغيل الخدمة).
/// </summary>
public sealed class InventoryTestKit : IDisposable
{
    private readonly string _baseDir;

    public InventoryTestKit()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "qb-tests-" + Guid.NewGuid().ToString("N"));
        SourceRoot = Path.Combine(_baseDir, "source");
        OutputRoot = Path.Combine(_baseDir, "output");
        Directory.CreateDirectory(SourceRoot);
        Directory.CreateDirectory(OutputRoot);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_baseDir, "test.db"),
            Pooling = false
        }.ToString();

        using var db = CreateDb();
        db.Database.Migrate();
    }

    public string SourceRoot { get; }
    public string OutputRoot { get; }
    public string ConnectionString { get; }
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero));

    public InventoryOptions Options { get; } = new() { MaxRetries = 3, RetryBaseDelaySeconds = 0 };

    public QuestionBankDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<QuestionBankDbContext>().UseSqlite(ConnectionString).Options);

    public InventoryScanner CreateScanner(QuestionBankDbContext db, ISourceFileSystem? fs = null)
    {
        Options.SourceRoot = SourceRoot;
        Options.OutputRoot = OutputRoot;
        return new InventoryScanner(db, fs ?? new PhysicalSourceFileSystem(),
            Microsoft.Extensions.Options.Options.Create(Options), Time, NullLogger<InventoryScanner>.Instance);
    }

    public async Task<ScanRun> ScanAsync(ISourceFileSystem? fs = null)
    {
        await using var db = CreateDb();
        return await CreateScanner(db, fs).RunAsync(CancellationToken.None);
    }

    public string WriteFile(string relativePath, string content = "محتوى اصطناعي")
    {
        var full = Path.Combine(SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string FullPath(string relativePath) =>
        Path.Combine(SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public List<SourceFile> Files()
    {
        using var db = CreateDb();
        return db.SourceFiles.AsNoTracking().OrderBy(f => f.RelativePath).ToList();
    }

    /// <summary>بصمة كل ملفات المصدر (المحتوى والحجم وتاريخ التعديل) لإثبات أن الجرد لم يغيّر شيئًا.</summary>
    public Dictionary<string, string> SnapshotSource() =>
        Directory.EnumerateFiles(SourceRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(SourceRoot, p),
                p =>
                {
                    var info = new FileInfo(p);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
                    return $"{hash}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                });

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_baseDir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// نظام ملفات يغلّف الحقيقي ويحقن أعطالًا: يرمي استثناءً لمجلدات معينة، أو يلغي بعد عدد من المجلدات،
/// أو يحاكي اختفاء الجذر (انقطاع الشبكة).
/// </summary>
public sealed class FaultInjectingFileSystem(string sourceRoot) : ISourceFileSystem
{
    private readonly PhysicalSourceFileSystem _inner = new();
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

    /// <summary>(المسار النسبي، رقم المحاولة) ← استثناء يُرمى أو null.</summary>
    public Func<string, int, Exception?>? Fault { get; set; }

    public bool RootAvailable { get; set; } = true;
    public int Listings { get; private set; }
    public Action<int>? AfterListing { get; set; }

    public bool DirectoryExists(string fullPath) =>
        (RootAvailable || Path.GetFullPath(fullPath) != Path.GetFullPath(sourceRoot)) && _inner.DirectoryExists(fullPath);

    public IReadOnlyList<SourceEntry> ListDirectory(string fullPath)
    {
        var rel = Path.GetRelativePath(sourceRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (rel == ".") rel = "";
        _calls[rel] = _calls.GetValueOrDefault(rel) + 1;

        var ex = Fault?.Invoke(rel, _calls[rel]);
        if (ex is not null)
            throw ex;

        var result = _inner.ListDirectory(fullPath);
        Listings++;
        AfterListing?.Invoke(Listings);
        return result;
    }

    public int CallsFor(string relativePath) => _calls.GetValueOrDefault(relativePath);

    public Func<string, Exception?>? ReadFault { get; set; }

    public byte[] ReadFile(string fullPath, long maxBytes)
    {
        var rel = Path.GetRelativePath(sourceRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (ReadFault?.Invoke(rel) is { } ex)
            throw ex;
        return _inner.ReadFile(fullPath, maxBytes);
    }
}
