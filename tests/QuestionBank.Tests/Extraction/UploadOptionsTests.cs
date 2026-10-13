using Microsoft.Extensions.Configuration;
using QuestionBank.Core.Extraction;

namespace QuestionBank.Tests.Extraction;

public sealed class UploadOptionsTests
{
    private static UploadOptions Bind(Dictionary<string, string?> values)
    {
        var options = new UploadOptions();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(UploadOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void ConfiguredExtensions_ReplaceDefaults_SoAnAdminCanRestrictFormats()
    {
        var o = Bind(new() { ["QuestionBank:Uploads:AllowedExtensions:0"] = "TXT", ["QuestionBank:Uploads:AllowedExtensions:1"] = ".gift" });
        Assert.Equal(["txt", "gift"], o.Extensions);
    }

    [Fact]
    public void NoConfiguration_UsesDefaults_WithoutDuplicates()
    {
        var o = Bind(new());
        Assert.Equal(UploadOptions.DefaultExtensions, o.Extensions);
    }

    [Fact]
    public void ZipIsNeverAPlainUploadExtension_ItIsControlledByAllowZip()
    {
        var o = Bind(new() { ["QuestionBank:Uploads:AllowedExtensions:0"] = "zip", ["QuestionBank:Uploads:AllowedExtensions:1"] = "txt" });
        Assert.Equal(["txt"], o.Extensions);
    }
}
