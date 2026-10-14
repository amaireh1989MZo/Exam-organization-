namespace QuestionBank.Core.Inventory;

/// <summary>يحوّل الاستثناءات إلى رموز ثابتة ويحدد ما يستحق إعادة المحاولة.</summary>
public static class InventoryErrors
{
    public static string Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => InventoryErrorCodes.AccessDenied,
        DirectoryNotFoundException or FileNotFoundException => InventoryErrorCodes.NotFound,
        PathTooLongException => InventoryErrorCodes.PathTooLong,
        IOException => InventoryErrorCodes.IoError,
        _ => InventoryErrorCodes.Unexpected
    };

    /// <summary>
    /// أخطاء الإدخال/الإخراج العامة قد تكون مؤقتة (انقطاع SMB، ملف مقفل) فتُعاد محاولتها.
    /// رفض الصلاحية والمسار غير الموجود لن يتغيرا بالإعادة.
    /// </summary>
    public static bool IsTransient(Exception ex) =>
        ex is IOException and not (DirectoryNotFoundException or FileNotFoundException or PathTooLongException);
}
