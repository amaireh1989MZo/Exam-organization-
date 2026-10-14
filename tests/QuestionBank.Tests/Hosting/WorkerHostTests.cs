using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using QuestionBank.Core.Data;
using QuestionBank.Core.Hosting;
using QuestionBank.Core.Inventory;
using QuestionBank.Tests.Inventory;

namespace QuestionBank.Tests.Hosting;

/// <summary>العاملان داخل مضيف كامل كما في خدمة Windows، والطلبات تصل عبر قاعدة البيانات فقط.</summary>
public sealed class WorkerHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qb-worker-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_dir, "src");
    private string Db => Path.Combine(_dir, "w.db");

    public WorkerHostTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Path.Combine(_dir, "out"));
        InventoryWorker.PollInterval = TimeSpan.FromMilliseconds(100);
        ExtractionWorker.IdleWait = TimeSpan.FromMilliseconds(100);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void Write(string rel, string content = "1. سؤال؟\nأ) س\n*ب) ص\n")
    {
        var full = Path.Combine(Source, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<IHost> StartAsync(ISourceFileSystem? fs = null, bool workers = true)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["QuestionBank:SourceRoot"] = Source,
            ["QuestionBank:OutputRoot"] = Path.Combine(_dir, "out"),
            ["QuestionBank:RetryBaseDelaySeconds"] = "0",
            ["ConnectionStrings:QuestionBank"] = $"Data Source={Db};Pooling=False"
        });
        builder.Services.AddQuestionBankCore(builder.Configuration);
        if (fs is not null)
            builder.Services.Replace(ServiceDescriptor.Singleton(fs));
        if (workers)
            builder.Services.AddQuestionBankWorkers();
        var host = builder.Build();
        await ServiceRegistration.InitializeDatabaseAsync(host.Services, builder.Configuration);
        await host.StartAsync();
        return host;
    }

    private QuestionBankDbContext NewDb() =>
        new(new DbContextOptionsBuilder<QuestionBankDbContext>().UseSqlite($"Data Source={Db};Pooling=False").Options);

    private async Task EnqueueAsync(string kind)
    {
        await using var db = NewDb();
        await new WorkerCommandService(db, TimeProvider.System).EnqueueAsync(kind, "CORP\\m", CancellationToken.None);
    }

    private static async Task Until(Func<Task<bool>> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task ScanCommandInDatabase_IsPickedUp_AndHeartbeatIsReported()
    {
        Write("أ/1.txt"); Write("ب/2.txt");
        using var host = await StartAsync();
        await EnqueueAsync(WorkerCommandKinds.Scan);

        await Until(async () => { await using var db = NewDb(); return await db.ScanRuns.AnyAsync(r => r.Status == ScanRunStatus.Completed); });

        await using var check = NewDb();
        Assert.Equal(2, await check.SourceFiles.CountAsync());
        var status = await new WorkerCommandService(check, TimeProvider.System).StatusAsync(WorkerCommandService.InventoryWorkerName, CancellationToken.None);
        Assert.True(status.Running);
        Assert.Equal(Environment.MachineName, status.Host);
        await host.StopAsync();
    }

    [Fact]
    public async Task RepeatedScanCommands_AreCoalescedIntoOneRun()
    {
        Write("أ/1.txt");
        using (var setup = await StartAsync(workers: false)) await setup.StopAsync(); // إنشاء القاعدة فقط
        await EnqueueAsync(WorkerCommandKinds.Scan);
        await EnqueueAsync(WorkerCommandKinds.Scan);
        await EnqueueAsync(WorkerCommandKinds.Scan);

        using var host = await StartAsync();
        await Until(async () => { await using var db = NewDb(); return await db.WorkerCommands.AllAsync(c => c.ProcessedAtUtc != null) && await db.ScanRuns.AnyAsync(r => r.Status == ScanRunStatus.Completed); });
        await Task.Delay(400);

        await using var check = NewDb();
        Assert.Equal(1, await check.ScanRuns.CountAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task UnfinishedRun_IsResumedAutomaticallyOnStartup()
    {
        for (var i = 0; i < 5; i++) Write($"م{i}/ملف.txt");
        using (var setup = await StartAsync(workers: false))
        {
            // عملية توقفت بعد مجلدين كأن الخدمة أُوقفت.
            using var cts = new CancellationTokenSource();
            var fs = new FaultInjectingFileSystem(Source) { AfterListing = n => { if (n == 2) cts.Cancel(); } };
            await using var scope = setup.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<QuestionBankDbContext>();
            var scanner = new InventoryScanner(db, fs, scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<InventoryOptions>>(),
                TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<InventoryScanner>.Instance);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.RunAsync(cts.Token));
            await setup.StopAsync();
        }

        using var host = await StartAsync();
        await Until(async () => { await using var db = NewDb(); return await db.ScanRuns.AnyAsync(r => r.Status == ScanRunStatus.Completed); });

        await using var check = NewDb();
        Assert.Equal(1, await check.ScanRuns.CountAsync());
        Assert.Equal(5, await check.SourceFiles.CountAsync());
        await host.StopAsync();
    }

    /// <summary>نظام ملفات بطيء: كل مجلد يستغرق وقتًا، فيمكن إرسال طلب التخلي أثناء الجرد.</summary>
    private sealed class SlowFileSystem : ISourceFileSystem
    {
        private readonly PhysicalSourceFileSystem _inner = new();
        public bool DirectoryExists(string fullPath) => _inner.DirectoryExists(fullPath);
        public byte[] ReadFile(string fullPath, long maxBytes) => _inner.ReadFile(fullPath, maxBytes);
        public IReadOnlyList<SourceEntry> ListDirectory(string fullPath)
        {
            Thread.Sleep(150);
            return _inner.ListDirectory(fullPath);
        }
    }

    [Fact]
    public async Task AbandonCommand_StopsARunningScan_AndItIsNotResumed()
    {
        for (var i = 0; i < 40; i++) Write($"م{i}/ملف.txt");
        using var host = await StartAsync(new SlowFileSystem());
        await EnqueueAsync(WorkerCommandKinds.Scan);
        await Until(async () => { await using var db = NewDb(); return await db.WorkerStates.AnyAsync(s => s.Name == "inventory" && s.Busy); });

        await EnqueueAsync(WorkerCommandKinds.Abandon);
        await Until(async () => { await using var db = NewDb(); return await db.ScanRuns.AnyAsync(r => r.Status == ScanRunStatus.Cancelled); });

        await using var check = NewDb();
        Assert.True(await check.SourceFiles.CountAsync() < 40);
        Assert.False(await check.ScanRuns.AnyAsync(r => r.Status == ScanRunStatus.Running));
        await host.StopAsync();
    }

    [Fact]
    public async Task ExtractionWorker_ProcessesQueuedFolders()
    {
        Write("إداري/أسئلة.txt");
        using var host = await StartAsync();
        await EnqueueAsync(WorkerCommandKinds.Scan);
        await Until(async () => { await using var db = NewDb(); return await db.ScanRuns.AnyAsync(r => r.Status == ScanRunStatus.Completed); });
        await using (var db = NewDb())
            await new FolderService(db, TimeProvider.System).EnqueueAsync("إداري", "u", CancellationToken.None);

        await Until(async () => { await using var db = NewDb(); return await db.Questions.AnyAsync(); });
        await using var check = NewDb();
        Assert.Equal(FileStatus.Completed, (await check.SourceFiles.SingleAsync()).Status);
        await host.StopAsync();
    }

    [Fact]
    public async Task Status_ReportsNotStarted_AndStaleHeartbeatAsStopped()
    {
        using (var setup = await StartAsync(workers: false)) await setup.StopAsync();
        await using var db = NewDb();
        var svc = new WorkerCommandService(db, TimeProvider.System);
        Assert.Equal("WorkerNotStarted", (await svc.StatusAsync("inventory", CancellationToken.None)).ProblemCode);

        db.WorkerStates.Add(new WorkerState { Name = "inventory", Host = "OLD", HeartbeatUtc = DateTime.UtcNow.AddMinutes(-5) });
        await db.SaveChangesAsync();
        var status = await svc.StatusAsync("inventory", CancellationToken.None);
        Assert.False(status.Running);
        Assert.Equal("WorkerStopped", status.ProblemCode);
    }
}
