using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Inventory;

namespace QuestionBank.Tests.Inventory;

public sealed class InventoryScannerTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private void SeedTree()
    {
        _kit.WriteFile("إداري/2023/نموذج-أ.docx");
        _kit.WriteFile("إداري/2023/نموذج-ب.DOCX");
        _kit.WriteFile("إداري/2024/بنك.xlsx");
        _kit.WriteFile("محاسبة/أسئلة.txt");
        _kit.WriteFile("ملاحظات");
    }

    [Fact]
    public async Task FirstScan_RecordsEveryFileWithRelativePathAndPendingStatus()
    {
        SeedTree();

        var run = await _kit.ScanAsync();

        Assert.Equal(ScanRunStatus.Completed, run.Status);
        var files = _kit.Files();
        Assert.Equal(
            ["إداري/2023/نموذج-أ.docx", "إداري/2023/نموذج-ب.DOCX", "إداري/2024/بنك.xlsx", "محاسبة/أسئلة.txt", "ملاحظات"],
            files.Select(f => f.RelativePath).Order(StringComparer.Ordinal).ToArray());
        Assert.All(files, f => Assert.Equal(FileStatus.Pending, f.Status));
        Assert.All(files, f => Assert.False(Path.IsPathRooted(f.RelativePath)));
        Assert.Equal("docx", files.Single(f => f.RelativePath.EndsWith("ب.DOCX")).Extension);
        Assert.Null(files.Single(f => f.RelativePath == "ملاحظات").Extension);
        Assert.Equal(5, run.FilesNew);
        Assert.Equal(5, run.DirectoriesCompleted); // الجذر، إداري، إداري/2023، إداري/2024، محاسبة
    }

    [Fact]
    public async Task Scan_DoesNotModifySourceFiles()
    {
        SeedTree();
        var before = _kit.SnapshotSource();

        await _kit.ScanAsync();
        await _kit.ScanAsync();

        Assert.Equal(before, _kit.SnapshotSource());
    }

    [Fact]
    public async Task Rescan_UnchangedFiles_ProducesNoDuplicatesAndKeepsStatus()
    {
        SeedTree();
        await _kit.ScanAsync();
        await using (var db = _kit.CreateDb())
        {
            await db.SourceFiles.ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FileStatus.Completed));
        }

        var second = await _kit.ScanAsync();

        var files = _kit.Files();
        Assert.Equal(5, files.Count);
        Assert.All(files, f => Assert.Equal(FileStatus.Completed, f.Status));
        Assert.Equal(0, second.FilesNew);
        Assert.Equal(0, second.FilesChanged);
        Assert.Equal(5, second.FilesSeen);
    }

    [Fact]
    public async Task Rescan_ChangedFile_IsResetToPendingForReprocessing()
    {
        SeedTree();
        await _kit.ScanAsync();
        await using (var db = _kit.CreateDb())
        {
            await db.SourceFiles.ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Status, FileStatus.Completed)
                .SetProperty(f => f.ContentHash, "old"));
        }
        File.AppendAllText(_kit.FullPath("محاسبة/أسئلة.txt"), " سؤال إضافي");

        var second = await _kit.ScanAsync();

        var changed = _kit.Files().Single(f => f.RelativePath == "محاسبة/أسئلة.txt");
        Assert.Equal(FileStatus.Pending, changed.Status);
        Assert.Null(changed.ContentHash);
        Assert.Equal(1, changed.ChangeCount);
        Assert.Equal(1, second.FilesChanged);
        Assert.All(_kit.Files().Where(f => f.Id != changed.Id), f => Assert.Equal(FileStatus.Completed, f.Status));
    }

    [Fact]
    public async Task Rescan_DeletedFile_IsMarkedMissingButNeverDeleted()
    {
        SeedTree();
        await _kit.ScanAsync();
        File.Delete(_kit.FullPath("إداري/2024/بنك.xlsx"));

        var second = await _kit.ScanAsync();

        var files = _kit.Files();
        Assert.Equal(5, files.Count);
        Assert.True(files.Single(f => f.RelativePath == "إداري/2024/بنك.xlsx").IsMissing);
        Assert.Equal(1, second.FilesMissing);

        _kit.WriteFile("إداري/2024/بنك.xlsx");
        await _kit.ScanAsync();
        Assert.False(_kit.Files().Single(f => f.RelativePath == "إداري/2024/بنك.xlsx").IsMissing);
    }

    [Fact]
    public async Task SymbolicLinks_AreNeverFollowed_EvenWhenPointingOutsideRoot()
    {
        SeedTree();
        var outside = Path.Combine(Path.GetDirectoryName(_kit.SourceRoot)!, "outside-secret");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "سري.docx"), "خارج الجذر");
        Directory.CreateSymbolicLink(_kit.FullPath("رابط-مجلد"), outside);
        File.CreateSymbolicLink(_kit.FullPath("رابط-ملف.docx"), Path.Combine(outside, "سري.docx"));

        var run = await _kit.ScanAsync();

        Assert.DoesNotContain(_kit.Files(), f => f.RelativePath.Contains("رابط") || f.RelativePath.Contains("سري"));
        Assert.Equal(2, run.LinksSkipped);
    }

    [Fact]
    public async Task InterruptedScan_ResumesWithNewContext_WithoutDuplicatesOrLostFiles()
    {
        for (var d = 0; d < 6; d++)
            for (var f = 0; f < 4; f++)
                _kit.WriteFile($"مجلد{d}/ملف{f}.docx");

        // التوقف بعد 3 مجلدات، كأن الخدمة أُوقفت أو انقطع التيار.
        using var cts = new CancellationTokenSource();
        var faulty = new FaultInjectingFileSystem(_kit.SourceRoot) { AfterListing = n => { if (n == 3) cts.Cancel(); } };
        await using (var db = _kit.CreateDb())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _kit.CreateScanner(db, faulty).RunAsync(cts.Token));
        }

        var partial = _kit.Files().Count;
        Assert.InRange(partial, 1, 23);

        // إعادة التشغيل بسياق جديد تستأنف العملية نفسها.
        var resumed = await _kit.ScanAsync();

        var files = _kit.Files();
        Assert.Equal(24, files.Count);
        Assert.Equal(24, files.Select(f => f.RelativePath).Distinct().Count());
        Assert.Equal(ScanRunStatus.Completed, resumed.Status);
        Assert.Equal(24, resumed.FilesNew);
        await using var check = _kit.CreateDb();
        Assert.Equal(1, await check.ScanRuns.CountAsync());
    }

    [Fact]
    public async Task AccessDeniedDirectory_IsRecordedAndScanContinues_AndItsFilesAreNotMarkedMissing()
    {
        SeedTree();
        await _kit.ScanAsync();
        var faulty = new FaultInjectingFileSystem(_kit.SourceRoot)
        {
            Fault = (rel, _) => rel == "إداري/2023" ? new UnauthorizedAccessException("denied") : null
        };

        var run = await _kit.ScanAsync(faulty);

        Assert.Equal(ScanRunStatus.Completed, run.Status);
        Assert.Equal(1, run.DirectoriesFailed);
        Assert.Equal(0, run.FilesMissing);
        Assert.Equal(1, faulty.CallsFor("إداري/2023")); // رفض الصلاحية لا يُعاد
        Assert.DoesNotContain(_kit.Files(), f => f.IsMissing);
        await using var db = _kit.CreateDb();
        var failed = await db.ScanDirectories.SingleAsync();
        Assert.Equal(InventoryErrorCodes.AccessDenied, failed.LastErrorCode);
    }

    [Fact]
    public async Task TransientIoError_IsRetriedAndThenSucceeds()
    {
        SeedTree();
        var faulty = new FaultInjectingFileSystem(_kit.SourceRoot)
        {
            Fault = (rel, attempt) => rel == "محاسبة" && attempt < 3 ? new IOException("network glitch") : null
        };

        var run = await _kit.ScanAsync(faulty);

        Assert.Equal(3, faulty.CallsFor("محاسبة"));
        Assert.Equal(0, run.DirectoriesFailed);
        Assert.Contains(_kit.Files(), f => f.RelativePath == "محاسبة/أسئلة.txt");
    }

    [Fact]
    public async Task TransientIoError_StopsAfterMaxRetries()
    {
        SeedTree();
        var faulty = new FaultInjectingFileSystem(_kit.SourceRoot)
        {
            Fault = (rel, _) => rel == "محاسبة" ? new IOException("still failing") : null
        };

        var run = await _kit.ScanAsync(faulty);

        Assert.Equal(_kit.Options.MaxRetries, faulty.CallsFor("محاسبة"));
        Assert.Equal(1, run.DirectoriesFailed);
        Assert.Equal(ScanRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Retry_WaitsForExponentialBackoff()
    {
        SeedTree();
        _kit.Options.RetryBaseDelaySeconds = 5;
        var faulty = new FaultInjectingFileSystem(_kit.SourceRoot)
        {
            Fault = (rel, attempt) => rel == "محاسبة" && attempt == 1 ? new IOException("glitch") : null
        };

        await using var db = _kit.CreateDb();
        var task = _kit.CreateScanner(db, faulty).RunAsync(CancellationToken.None);

        // الجذر وإداري و2023 و2024 تنجح، ومحاسبة تنتظر موعد إعادة المحاولة.
        await WaitUntil(() => faulty.CallsFor("محاسبة") == 1 && faulty.Listings == 4);
        await Task.Delay(100);
        Assert.False(task.IsCompleted);

        _kit.Time.Advance(TimeSpan.FromSeconds(5));
        var run = await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, faulty.CallsFor("محاسبة"));
        Assert.Equal(ScanRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task SourceRootDisappearing_DoesNotConsumeRetries_AndRunStaysResumable()
    {
        SeedTree();
        var faulty = new FaultInjectingFileSystem(_kit.SourceRoot);
        faulty.Fault = (rel, _) =>
        {
            if (rel != "محاسبة") return null;
            faulty.RootAvailable = false;
            return new IOException("network path not found");
        };

        await using (var db = _kit.CreateDb())
        {
            await Assert.ThrowsAsync<SourceUnavailableException>(() => _kit.CreateScanner(db, faulty).RunAsync(CancellationToken.None));
            var dir = await db.ScanDirectories.AsNoTracking().SingleAsync(d => d.RelativePath == "محاسبة");
            Assert.Equal(0, dir.Attempts);
            Assert.Equal(ScanDirectoryStatus.Pending, dir.Status);
        }

        var run = await _kit.ScanAsync();
        Assert.Equal(ScanRunStatus.Completed, run.Status);
        Assert.Equal(5, _kit.Files().Count);
    }

    [Fact]
    public async Task UnreadableEntry_IsRecordedAsFailed_OthersContinue()
    {
        SeedTree();
        var fs = new StubEntriesFileSystem(_kit.SourceRoot, extra: new SourceEntry("مقفل.docx", false, 0, default, false, InventoryErrorCodes.AccessDenied));

        var run = await _kit.ScanAsync(fs);

        var locked = _kit.Files().Single(f => f.RelativePath == "مقفل.docx");
        Assert.Equal(FileStatus.Failed, locked.Status);
        Assert.Equal(InventoryErrorCodes.AccessDenied, locked.LastErrorCode);
        Assert.Equal(1, run.EntryErrors);
        Assert.Equal(6, _kit.Files().Count);
    }

    [Fact]
    public async Task AbandonedRun_IsNotResumed_NextScanStartsFresh()
    {
        SeedTree();
        using var cts = new CancellationTokenSource();
        var faulty = new FaultInjectingFileSystem(_kit.SourceRoot) { AfterListing = n => { if (n == 1) cts.Cancel(); } };
        await using (var db = _kit.CreateDb())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _kit.CreateScanner(db, faulty).RunAsync(cts.Token));
            Assert.True(await _kit.CreateScanner(db).AbandonActiveRunAsync(CancellationToken.None));
        }

        var run = await _kit.ScanAsync();

        await using var check = _kit.CreateDb();
        Assert.Equal(2, await check.ScanRuns.CountAsync());
        Assert.Equal(ScanRunStatus.Cancelled, (await check.ScanRuns.FirstAsync(r => r.Id != run.Id)).Status);
        Assert.Equal(5, _kit.Files().Count);
    }

    [Fact]
    public async Task InvalidConfiguration_IsRejectedBeforeTouchingAnything()
    {
        await using var db = _kit.CreateDb();
        var scanner = new InventoryScanner(db, new PhysicalSourceFileSystem(),
            Microsoft.Extensions.Options.Options.Create(new InventoryOptions
            {
                SourceRoot = _kit.SourceRoot,
                OutputRoot = Path.Combine(_kit.SourceRoot, "output")
            }),
            _kit.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<InventoryScanner>.Instance);

        var ex = await Assert.ThrowsAsync<InventoryConfigurationException>(() => scanner.RunAsync(CancellationToken.None));

        Assert.Single(ex.Errors);
        Assert.Equal(0, await db.ScanRuns.CountAsync());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    /// <summary>يضيف عنصرًا متعذر القراءة إلى محتوى الجذر الحقيقي.</summary>
    private sealed class StubEntriesFileSystem(string root, SourceEntry extra) : ISourceFileSystem
    {
        private readonly PhysicalSourceFileSystem _inner = new();
        public bool DirectoryExists(string fullPath) => _inner.DirectoryExists(fullPath);

        public IReadOnlyList<SourceEntry> ListDirectory(string fullPath)
        {
            var list = _inner.ListDirectory(fullPath).ToList();
            if (Path.GetFullPath(fullPath) == Path.GetFullPath(root))
                list.Add(extra);
            return list;
        }
    }
}
