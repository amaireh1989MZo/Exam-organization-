using QuestionBank.Core.Inventory;

namespace QuestionBank.Tests.Inventory;

public sealed class SourcePathsTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "qb-root"));

    [Theory]
    [InlineData("../خارج")]
    [InlineData("مجلد/../../خارج")]
    [InlineData("مجلد//ملف")]
    [InlineData("./ملف")]
    [InlineData("مجلد/")]
    public void ToFullPath_RejectsTraversalAndMalformedPaths(string relative) =>
        Assert.Null(SourcePaths.ToFullPath(Root, relative));

    [Fact]
    public void ToFullPath_RejectsRootedPaths()
    {
        var rooted = OperatingSystem.IsWindows() ? @"C:\Windows" : "/etc/passwd";
        Assert.Null(SourcePaths.ToFullPath(Root, rooted));
    }

    [Fact]
    public void ToFullPath_ResolvesValidPathInsideRoot()
    {
        var full = SourcePaths.ToFullPath(Root, "إداري/2023/نموذج.docx");
        Assert.Equal(Path.Combine(Root, "إداري", "2023", "نموذج.docx"), full);
        Assert.Equal(Root, SourcePaths.ToFullPath(Root, ""));
    }

    [Fact]
    public void IsSameOrInside_DoesNotConfusePrefixSiblings()
    {
        Assert.False(SourcePaths.IsSameOrInside(Root + "-other", Root));
        Assert.True(SourcePaths.IsSameOrInside(Path.Combine(Root, "a"), Root));
    }

    [Fact]
    public void Validate_RejectsOutputInsideSourceAndSourceInsideOutput()
    {
        var source = Path.Combine(Root, "src");
        var o1 = new InventoryOptions { SourceRoot = source, OutputRoot = Path.Combine(source, "out") };
        var o2 = new InventoryOptions { SourceRoot = Path.Combine(Root, "out", "src"), OutputRoot = Path.Combine(Root, "out") };
        var o3 = new InventoryOptions { SourceRoot = source, OutputRoot = source };

        Assert.Single(SourcePaths.Validate(o1, _ => true));
        Assert.Single(SourcePaths.Validate(o2, _ => true));
        Assert.Single(SourcePaths.Validate(o3, _ => true));
    }

    [Fact]
    public void Validate_RejectsMissingRelativeAndUnavailableRoots()
    {
        Assert.Equal(2, SourcePaths.Validate(new InventoryOptions(), _ => true).Count);
        Assert.NotEmpty(SourcePaths.Validate(new InventoryOptions { SourceRoot = "relative/path", OutputRoot = Path.Combine(Root, "o") }, _ => true));
        Assert.NotEmpty(SourcePaths.Validate(new InventoryOptions { SourceRoot = Path.Combine(Root, "s"), OutputRoot = Path.Combine(Root, "o") }, _ => false));
        Assert.Empty(SourcePaths.Validate(new InventoryOptions { SourceRoot = Path.Combine(Root, "s"), OutputRoot = Path.Combine(Root, "o") }, _ => true));
    }
}
