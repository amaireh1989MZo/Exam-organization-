using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestionBank.Core.Data;
using QuestionBank.Core.Inventory;

namespace QuestionBank.Core.Extraction;

public sealed class UploadOptions
{
    public const string SectionName = "QuestionBank:Uploads";

    [Range(1, 2048)]
    public int MaxFileSizeMb { get; set; } = 50;

    public static readonly string[] DefaultExtensions = ["docx", "xlsx", "xls", "doc", "pdf", "txt", "md", "gift"];

    /// <summary>
    /// الصيغ المقبولة كما في الإعدادات. تبدأ فارغة عمدًا: ربط المصفوفات في .NET يضيف قيم الإعدادات إلى القيم الافتراضية
    /// ولا يستبدلها، فلو كانت لها قيمة افتراضية لما استطاع المسؤول حذف صيغة. استخدم Extensions.
    /// </summary>
    public string[] AllowedExtensions { get; set; } = [];

    /// <summary>الصيغ الفعّالة: ما في الإعدادات (بلا تكرار وبأحرف صغيرة)، أو الافتراضية إذا لم يُضبط شيء.</summary>
    public IReadOnlyList<string> Extensions =>
        (AllowedExtensions.Length > 0 ? AllowedExtensions : DefaultExtensions)
        .Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0 && e != "zip").Distinct().ToList();

    /// <summary>قبول ملفات ZIP وفكّها بأمان. يمكن إيقافه من الإعدادات.</summary>
    public bool AllowZip { get; set; } = true;

    [Range(1, 100_000)]
    public int MaxZipEntries { get; set; } = 2000;

    /// <summary>الحجم الكلي بعد الفك. حماية من "قنبلة ZIP".</summary>
    [Range(1, 100_000)]
    public int MaxZipTotalMb { get; set; } = 1024;

    /// <summary>أقصى نسبة بين الحجم بعد الفك وقبله لعنصر واحد. الملفات النصية العادية أقل من 10 غالبًا.</summary>
    [Range(2, 10_000)]
    public int MaxCompressionRatio { get; set; } = 100;

    public long MaxBytes => MaxFileSizeMb * 1024L * 1024L;
    public long MaxZipTotalBytes => MaxZipTotalMb * 1024L * 1024L;
}

public static class UploadErrorCodes
{
    public const string ExtensionNotAllowed = "ExtensionNotAllowed";
    public const string TooLarge = "TooLarge";
    public const string Empty = "Empty";
    public const string OutputUnavailable = "OutputUnavailable";
    public const string InvalidArchive = "InvalidArchive";
    public const string ArchiveTooLarge = "ArchiveTooLarge";
    public const string TooManyEntries = "TooManyEntries";
    public const string SuspiciousCompression = "SuspiciousCompression";
    public const string NestedArchive = "NestedArchive";
}

public sealed record ArchiveSkip(string Entry, string Code, string Message);

public sealed record ArchiveResult(IReadOnlyList<UploadResult> Items, IReadOnlyList<ArchiveSkip> Skipped, string? ErrorCode, string? Message);

public sealed record UploadView(
    long Id, string OriginalName, string? ClientPath, string? Extension, long SizeBytes, string Status,
    string? LastErrorCode, int QuestionCount, string UploadedBy, DateTime UploadedAtUtc, bool Supported);

public sealed record UploadResult(UploadView? Upload, bool Duplicate, string? ErrorCode, string? Message);

/// <summary>
/// يحفظ الملفات المرفوعة في مجلد النتائج (وليس المصدر) باسم بصمتها، ويكشف التكرار بالبصمة،
/// ثم يستخرج الأسئلة فورًا إذا كان للصيغة قارئ.
/// </summary>
public sealed class UploadService(
    QuestionBankDbContext db,
    IOptions<InventoryOptions> inventoryOptions,
    IOptions<UploadOptions> uploadOptions,
    ExtractionService extraction,
    TimeProvider time)
{
    public async Task<UploadResult> SaveAsync(
        Stream content, string fileName, string? clientPath, string uploadedBy, CancellationToken cancellationToken)
    {
        var opt = uploadOptions.Value;
        var name = SafeDisplayName(fileName, 255);
        var ext = ExtensionOf(name);
        if (ext is null || !opt.Extensions.Contains(ext))
            return Fail(UploadErrorCodes.ExtensionNotAllowed, "صيغة الملف غير مقبولة.");

        var root = UploadsRoot();
        if (root is null)
            return Fail(UploadErrorCodes.OutputUnavailable, "مجلد النتائج غير مضبوط أو غير صالح.");

        var tmpDir = Path.Combine(root, ".tmp");
        Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));
        string sha;
        long size = 0;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > opt.MaxBytes)
                        return Fail(UploadErrorCodes.TooLarge, $"حجم الملف أكبر من {opt.MaxFileSizeMb} ميغابايت.");
                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (size == 0)
                return Fail(UploadErrorCodes.Empty, "الملف فارغ.");
            sha = Convert.ToHexString(hasher.GetHashAndReset());

            var existing = await db.Uploads.AsNoTracking().FirstOrDefaultAsync(u => u.Sha256 == sha, cancellationToken);
            if (existing is not null)
                return new UploadResult(ToView(existing), true, null, "هذا الملف رُفع سابقًا بالمحتوى نفسه.");

            var storedName = $"{sha[..2]}/{sha}.{ext}";
            var final = Path.Combine(root, sha[..2], $"{sha}.{ext}");
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (!File.Exists(final))
                File.Move(tmp, final);

            var upload = new Upload
            {
                OriginalName = name,
                ClientPath = clientPath is null ? null : SafeDisplayName(clientPath, 1024, keepSeparators: true),
                Extension = ext,
                SizeBytes = size,
                Sha256 = sha,
                StoredName = storedName,
                UploadedBy = uploadedBy,
                UploadedAtUtc = time.GetUtcNow().UtcDateTime
            };
            db.Uploads.Add(upload);
            await db.SaveChangesAsync(cancellationToken);

            if (extraction.IsSupported(ext))
                await ExtractStoredAsync(upload, final, cancellationToken);
            return new UploadResult(ToView(upload), false, null, null);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    public const string NeedsColumnMapping = "NeedsColumnMapping";

    private async Task ExtractStoredAsync(Upload upload, string path, CancellationToken cancellationToken)
    {
        var outcome = await extraction.ExtractAsync(await File.ReadAllBytesAsync(path, cancellationToken), upload.Extension, null, upload.Id, cancellationToken);
        upload.Status = outcome.ErrorCode is not null ? FileStatus.Failed : outcome.NeedsMapping ? FileStatus.NeedsReview : FileStatus.Completed;
        upload.LastErrorCode = outcome.ErrorCode ?? (outcome.NeedsMapping ? NeedsColumnMapping : null);
        upload.QuestionCount = outcome.QuestionCount;
        upload.ProcessedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>مسار الملف المرفوع المحفوظ، أو null.</summary>
    public async Task<(Upload Upload, string Path)?> StoredAsync(long id, CancellationToken cancellationToken)
    {
        var upload = await db.Uploads.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        var root = UploadsRoot();
        if (upload is null || root is null)
            return null;
        var full = Path.GetFullPath(Path.Combine(root, upload.StoredName));
        return SourcePaths.IsSameOrInside(full, root) && File.Exists(full) ? (upload, full) : null;
    }

    /// <summary>يعيد استخراج ملف مرفوع (بعد حفظ قالب أعمدة مثلًا).</summary>
    public async Task<UploadView?> ReextractAsync(long id, CancellationToken cancellationToken)
    {
        var stored = await StoredAsync(id, cancellationToken);
        if (stored is null || !extraction.IsSupported(stored.Value.Upload.Extension))
            return null;
        await ExtractStoredAsync(stored.Value.Upload, stored.Value.Path, cancellationToken);
        return ToView(stored.Value.Upload);
    }

    public static bool IsArchive(string fileName) =>
        string.Equals(Path.GetExtension(SafeDisplayName(fileName, 255)), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// يفك ملف ZIP بأمان: حدود لعدد العناصر والحجم الكلي ونسبة الضغط، وقراءة فعلية محدودة لأن الأحجام المعلنة
    /// داخل ZIP قد تكون مزيفة، ورفض ZIP داخل ZIP. أسماء العناصر لا تُستخدم كمسارات أبدًا؛ كل عنصر يمر بـ SaveAsync.
    /// </summary>
    public async Task<ArchiveResult> SaveArchiveAsync(Stream content, string fileName, string uploadedBy, CancellationToken cancellationToken)
    {
        var opt = uploadOptions.Value;
        if (!opt.AllowZip)
            return new ArchiveResult([], [], UploadErrorCodes.ExtensionNotAllowed, "ملفات ZIP غير مقبولة في الإعدادات.");
        var root = UploadsRoot();
        if (root is null)
            return new ArchiveResult([], [], UploadErrorCodes.OutputUnavailable, "مجلد النتائج غير مضبوط أو غير صالح.");

        var archiveName = SafeDisplayName(fileName, 255);
        var tmpDir = Path.Combine(root, ".tmp");
        Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                try
                {
                    await new LimitedReadStream(content, opt.MaxBytes).CopyToAsync(output, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    return new ArchiveResult([], [], UploadErrorCodes.TooLarge, $"حجم الملف أكبر من {opt.MaxFileSizeMb} ميغابايت.");
                }
            }

            ZipArchive zip;
            try
            {
                zip = ZipFile.OpenRead(tmp);
            }
            catch (InvalidDataException)
            {
                return new ArchiveResult([], [], UploadErrorCodes.InvalidArchive, "الملف ليس ZIP صالحًا.");
            }

            using (zip)
            {
                var files = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
                if (files.Count > opt.MaxZipEntries)
                    return new ArchiveResult([], [], UploadErrorCodes.TooManyEntries, $"عدد الملفات داخل ZIP أكبر من {opt.MaxZipEntries}.");
                if (files.Sum(e => e.Length) > opt.MaxZipTotalBytes)
                    return new ArchiveResult([], [], UploadErrorCodes.ArchiveTooLarge, $"حجم محتوى ZIP بعد الفك أكبر من {opt.MaxZipTotalMb} ميغابايت.");

                var items = new List<UploadResult>();
                var skipped = new List<ArchiveSkip>();
                long totalRead = 0;
                foreach (var entry in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryName = SafeDisplayName(entry.FullName, 1024, keepSeparators: true);
                    var ext = ExtensionOf(SafeDisplayName(entry.FullName, 255));
                    if (ext == "zip")
                    {
                        skipped.Add(new(entryName, UploadErrorCodes.NestedArchive, "ملف ZIP داخل ZIP غير مقبول."));
                        continue;
                    }
                    if (ext is null || !opt.Extensions.Contains(ext))
                    {
                        skipped.Add(new(entryName, UploadErrorCodes.ExtensionNotAllowed, "صيغة غير مقبولة."));
                        continue;
                    }
                    if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > opt.MaxCompressionRatio)
                    {
                        skipped.Add(new(entryName, UploadErrorCodes.SuspiciousCompression, "نسبة ضغط غير طبيعية."));
                        continue;
                    }

                    // القراءة الفعلية محدودة بالأصغر من: حد الملف، والمتبقي من الحد الكلي، والحجم المعلن + 1.
                    var limit = Math.Min(Math.Min(opt.MaxBytes, opt.MaxZipTotalBytes - totalRead), entry.Length + 1);
                    try
                    {
                        await using var entryStream = entry.Open();
                        var limited = new LimitedReadStream(entryStream, limit);
                        var result = await SaveAsync(limited, entry.Name, $"{archiveName}/{entryName}", uploadedBy, cancellationToken);
                        totalRead += limited.BytesRead;
                        if (result.ErrorCode is not null)
                            skipped.Add(new(entryName, result.ErrorCode, result.Message ?? ""));
                        else
                            items.Add(result);
                    }
                    catch (InvalidDataException)
                    {
                        skipped.Add(new(entryName, UploadErrorCodes.SuspiciousCompression, "حجم العنصر الفعلي يخالف المعلن أو يتجاوز الحد."));
                    }
                }
                return new ArchiveResult(items, skipped, null, null);
            }
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    public async Task<IReadOnlyList<UploadView>> ListAsync(int take, CancellationToken cancellationToken)
    {
        var rows = await db.Uploads.AsNoTracking().OrderByDescending(u => u.Id).Take(take).ToListAsync(cancellationToken);
        return rows.Select(ToView).ToList();
    }

    /// <summary>مجلد الرفع داخل مجلد النتائج، بشرط ألا يكون مجلد النتائج داخل المصدر.</summary>
    private string? UploadsRoot()
    {
        var o = inventoryOptions.Value;
        if (string.IsNullOrWhiteSpace(o.OutputRoot) || !Path.IsPathFullyQualified(o.OutputRoot))
            return null;
        var output = SourcePaths.NormalizeRoot(o.OutputRoot);
        if (!string.IsNullOrWhiteSpace(o.SourceRoot) && Path.IsPathFullyQualified(o.SourceRoot)
            && SourcePaths.IsSameOrInside(output, SourcePaths.NormalizeRoot(o.SourceRoot)))
            return null;
        return Path.Combine(output, "uploads");
    }

    private UploadView ToView(Upload u) => new(u.Id, u.OriginalName, u.ClientPath, u.Extension, u.SizeBytes,
        u.Status.ToString(), u.LastErrorCode, u.QuestionCount, u.UploadedBy, u.UploadedAtUtc, extraction.IsSupported(u.Extension));

    private static UploadResult Fail(string code, string message) => new(null, false, code, message);

    /// <summary>اسم للعرض فقط: آخر مقطع من المسار، بلا محارف تحكم، بطول محدود. لا يُستخدم في أي مسار على القرص.</summary>
    public static string SafeDisplayName(string value, int maxLength, bool keepSeparators = false)
    {
        var s = keepSeparators ? value.Replace('\\', '/') : value.Split('/', '\\').LastOrDefault() ?? "";
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0)
            s = "ملف";
        return s.Length > maxLength ? s[..maxLength] : s;
    }

    private static string? ExtensionOf(string name)
    {
        var ext = Path.GetExtension(name);
        return string.IsNullOrEmpty(ext) || ext.Length < 2 ? null : ext[1..].ToLowerInvariant();
    }
}
