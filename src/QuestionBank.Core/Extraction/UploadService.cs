using System.ComponentModel.DataAnnotations;
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

    /// <summary>الصيغ المقبولة في الرفع. ZIP غير مقبول حتى يُبنى فك آمن له (حماية من الملفات المضغوطة الخبيثة).</summary>
    public string[] AllowedExtensions { get; set; } = ["docx", "xlsx", "xls", "doc", "pdf", "txt", "md", "gift"];

    public long MaxBytes => MaxFileSizeMb * 1024L * 1024L;
}

public static class UploadErrorCodes
{
    public const string ExtensionNotAllowed = "ExtensionNotAllowed";
    public const string TooLarge = "TooLarge";
    public const string Empty = "Empty";
    public const string OutputUnavailable = "OutputUnavailable";
}

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
        if (ext is null || !opt.AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
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
            {
                var outcome = await extraction.ExtractAsync(await File.ReadAllBytesAsync(final, cancellationToken), ext, null, upload.Id, cancellationToken);
                upload.Status = outcome.ErrorCode is null ? FileStatus.Completed : FileStatus.Failed;
                upload.LastErrorCode = outcome.ErrorCode;
                upload.QuestionCount = outcome.QuestionCount;
                upload.ProcessedAtUtc = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
            }
            return new UploadResult(ToView(upload), false, null, null);
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
