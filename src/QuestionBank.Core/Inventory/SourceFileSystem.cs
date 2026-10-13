namespace QuestionBank.Core.Inventory;

/// <summary>عنصر واحد داخل مجلد، بالمعلومات التي يحتاجها الجرد فقط.</summary>
/// <param name="IsLink">رابط رمزي أو junction. لا يتبعه الجرد أبدًا.</param>
/// <param name="ErrorCode">إذا تعذرت قراءة معلومات هذا العنصر وحده؛ باقي المجلد يُجرد طبيعيًا.</param>
public sealed record SourceEntry(
    string Name, bool IsDirectory, long Length, DateTime LastWriteUtc, bool IsLink, string? ErrorCode = null);

/// <summary>
/// واجهة قراءة فقط لنظام ملفات المصدر. لا توجد فيها أي عملية كتابة أو حذف أو إعادة تسمية،
/// فلا يستطيع الجرد تعديل المصدر حتى بالخطأ.
/// </summary>
public interface ISourceFileSystem
{
    bool DirectoryExists(string fullPath);

    /// <summary>يعدّد محتوى مجلد واحد فقط (بدون تكرار تنازلي). يرمي الاستثناء عند تعذر فتح المجلد.</summary>
    IReadOnlyList<SourceEntry> ListDirectory(string fullPath);

    /// <summary>يقرأ محتوى ملف للقراءة فقط، ويسمح لبرامج أخرى بفتحه في الوقت نفسه. يرفض ما يتجاوز الحد.</summary>
    byte[] ReadFile(string fullPath, long maxBytes);
}

public sealed class PhysicalSourceFileSystem : ISourceFileSystem
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        // لا نتخطى المخفي أو ملفات النظام: ملفات أسئلة قديمة قد تكون مخفية. التصفية لاحقًا حسب الامتداد.
        AttributesToSkip = 0
    };

    public bool DirectoryExists(string fullPath) => Directory.Exists(fullPath);

    public byte[] ReadFile(string fullPath, long maxBytes)
    {
        // FileAccess.Read فقط، وFileShare يسمح للآخرين بالقراءة والكتابة: الجرد لا يقفل ملفات الموظفين.
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
            throw new IOException("File exceeds the configured size limit.");
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public IReadOnlyList<SourceEntry> ListDirectory(string fullPath)
    {
        var result = new List<SourceEntry>();
        foreach (var info in new DirectoryInfo(fullPath).EnumerateFileSystemInfos("*", Options))
        {
            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            try
            {
                // LinkTarget غير فارغ للروابط الرمزية والـ junctions فقط. لا نعتمد على سمة ReparsePoint وحدها،
                // لأن Windows Server يضعها أيضًا على الملفات المضغوطة بخاصية Data Deduplication وهي ملفات حقيقية.
                var isLink = info.LinkTarget is not null;
                var length = !isDirectory && info is FileInfo file ? file.Length : 0;
                result.Add(new SourceEntry(info.Name, isDirectory, length, info.LastWriteTimeUtc, isLink));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add(new SourceEntry(info.Name, isDirectory, 0, default, false, InventoryErrors.Classify(ex)));
            }
        }
        return result;
    }
}
