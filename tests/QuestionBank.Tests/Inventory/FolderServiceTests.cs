using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Inventory;

namespace QuestionBank.Tests.Inventory;

public sealed class FolderServiceTests : IDisposable
{
    private readonly InventoryTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private async Task SeedAsync()
    {
        _kit.WriteFile("إداري/2023/أ.docx");
        _kit.WriteFile("إداري/2023/ب.docx");
        _kit.WriteFile("إداري/2024/ج.xlsx");
        _kit.WriteFile("إداري/فهرس.txt");
        _kit.WriteFile("محاسبة/د.docx");
        _kit.WriteFile("Admin/x.docx");
        _kit.WriteFile("admin-old/y.docx");
        _kit.WriteFile("جذر.txt");
        await _kit.ScanAsync();
    }

    private FolderService Service(out Microsoft.EntityFrameworkCore.DbContext db)
    {
        var ctx = _kit.CreateDb();
        db = ctx;
        return new FolderService(ctx, _kit.Time);
    }

    [Fact]
    public async Task Root_ListsTopFoldersWithRecursiveCounts()
    {
        await SeedAsync();
        var svc = Service(out var db);
        using var _ = db;

        var root = await svc.ListAsync("", CancellationToken.None);

        Assert.NotNull(root);
        Assert.Equal(1, root!.DirectFiles);
        var admin = root.Folders.Single(f => f.Name == "إداري");
        Assert.Equal(4, admin.FileCount);
        Assert.Equal(4, admin.Pending);
        Assert.True(admin.HasChildren);
        Assert.False(root.Folders.Single(f => f.Name == "محاسبة").HasChildren);
    }

    [Fact]
    public async Task SubFolder_ListsChildrenAndDirectFiles_AndStatusesAreCounted()
    {
        await SeedAsync();
        await using (var db = _kit.CreateDb())
        {
            await db.SourceFiles.Where(f => f.RelativePath == "إداري/2023/أ.docx")
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FileStatus.Completed));
            await db.SourceFiles.Where(f => f.RelativePath == "إداري/2023/ب.docx")
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FileStatus.Failed));
        }
        var svc = Service(out var ctx);
        using var _ = ctx;

        var listing = await svc.ListAsync("إداري", CancellationToken.None);

        Assert.Equal(1, listing!.DirectFiles);
        var y2023 = listing.Folders.Single(f => f.Name == "2023");
        Assert.Equal("إداري/2023", y2023.RelativePath);
        Assert.Equal((2, 1, 1, 0), (y2023.FileCount, y2023.Completed, y2023.Failed, y2023.Pending));
    }

    [Fact]
    public async Task Listing_DoesNotMixFoldersSharingAPrefixOrDifferingInCase()
    {
        await SeedAsync();
        var svc = Service(out var db);
        using var _ = db;

        var admin = await svc.ListAsync("Admin", CancellationToken.None);

        Assert.Equal(1, admin!.DirectFiles);
        Assert.Empty(admin.Folders);
        Assert.Null(await svc.ListAsync("admin", CancellationToken.None));
    }

    [Theory]
    [InlineData("../خارج")]
    [InlineData("إداري/../محاسبة")]
    [InlineData("إداري\\2023")]
    [InlineData("/etc")]
    [InlineData("إداري/")]
    public async Task InvalidPaths_AreRejected(string path)
    {
        await SeedAsync();
        var svc = Service(out var db);
        using var _ = db;

        Assert.Null(await svc.ListAsync(path, CancellationToken.None));
        Assert.Equal(EnqueueOutcome.InvalidPath, (await svc.EnqueueAsync(path, "CORP\\u", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task MissingFiles_AreExcludedFromTree()
    {
        await SeedAsync();
        File.Delete(_kit.FullPath("محاسبة/د.docx"));
        await _kit.ScanAsync();
        var svc = Service(out var db);
        using var _ = db;

        var root = await svc.ListAsync("", CancellationToken.None);

        Assert.DoesNotContain(root!.Folders, f => f.Name == "محاسبة");
        Assert.Null(await svc.ListAsync("محاسبة", CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_RecordsRequester_CountsFiles_AndIgnoresDuplicates()
    {
        await SeedAsync();
        var svc = Service(out var db);
        using var _ = db;

        var (first, entry) = await svc.EnqueueAsync("إداري", "CORP\\manager", CancellationToken.None);
        var (second, again) = await svc.EnqueueAsync("إداري", "CORP\\other", CancellationToken.None);

        Assert.Equal(EnqueueOutcome.Added, first);
        Assert.Equal(4, entry!.FileCount);
        Assert.Equal("CORP\\manager", entry.RequestedBy);
        Assert.Equal(EnqueueOutcome.AlreadyQueued, second);
        Assert.Equal(entry.Id, again!.Id);
        Assert.Single(await svc.GetQueueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_UnknownFolder_IsRejected_AndRootIsAllowed()
    {
        await SeedAsync();
        var svc = Service(out var db);
        using var _ = db;

        Assert.Equal(EnqueueOutcome.UnknownFolder, (await svc.EnqueueAsync("غير-موجود", "u", CancellationToken.None)).Outcome);
        var (outcome, entry) = await svc.EnqueueAsync("", "u", CancellationToken.None);
        Assert.Equal(EnqueueOutcome.Added, outcome);
        Assert.Equal(8, entry!.FileCount);
    }

    [Fact]
    public async Task Cancel_RemovesFromQueue_AndAllowsRequeue()
    {
        await SeedAsync();
        var svc = Service(out var db);
        using var _ = db;
        var (_, entry) = await svc.EnqueueAsync("محاسبة", "u", CancellationToken.None);

        Assert.True(await svc.CancelAsync(entry!.Id, CancellationToken.None));
        Assert.False(await svc.CancelAsync(entry.Id, CancellationToken.None));
        Assert.Empty(await svc.GetQueueAsync(CancellationToken.None));
        Assert.Equal(EnqueueOutcome.Added, (await svc.EnqueueAsync("محاسبة", "u", CancellationToken.None)).Outcome);
    }
}
