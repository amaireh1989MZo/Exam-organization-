namespace QuestionBank.Core.Inventory;

/// <summary>
/// قواعد المسارات: كل ما يُخزَّن نسبيٌّ بفاصل '/'، وكل مسار كامل يُتحقَّق أنه داخل الجذر قبل استخدامه.
/// </summary>
public static class SourcePaths
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>يتحقق من إعدادات الجذرين ويعيد قائمة أخطاء مفهومة (بدون كشف المسارات في السجلات).</summary>
    public static IReadOnlyList<string> Validate(InventoryOptions options, Func<string, bool> directoryExists)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.SourceRoot))
            errors.Add("SourceRoot غير مضبوط.");
        else if (!Path.IsPathFullyQualified(options.SourceRoot))
            errors.Add("SourceRoot يجب أن يكون مسارًا كاملًا (UNC أو مسار مطلق).");
        else if (!directoryExists(options.SourceRoot))
            errors.Add("SourceRoot غير متاح. تحقق من مسار UNC وصلاحيات حساب الخدمة.");

        if (string.IsNullOrWhiteSpace(options.OutputRoot))
            errors.Add("OutputRoot غير مضبوط.");
        else if (!Path.IsPathFullyQualified(options.OutputRoot))
            errors.Add("OutputRoot يجب أن يكون مسارًا كاملًا.");

        if (errors.Count == 0)
        {
            var source = NormalizeRoot(options.SourceRoot);
            var output = NormalizeRoot(options.OutputRoot);
            if (IsSameOrInside(output, source))
                errors.Add("OutputRoot لا يجوز أن يكون داخل SourceRoot أو مساويًا له.");
            else if (IsSameOrInside(source, output))
                errors.Add("SourceRoot لا يجوز أن يكون داخل OutputRoot.");
        }

        return errors;
    }

    public static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>يعيد المسار الكامل لمسار نسبي مخزَّن، أو null إذا خرج عن الجذر بأي طريقة.</summary>
    public static string? ToFullPath(string normalizedRoot, string relativePath)
    {
        if (relativePath.Length == 0)
            return normalizedRoot;
        if (relativePath.Contains('\0') || Path.IsPathRooted(relativePath))
            return null;

        var segments = relativePath.Split('/');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".."))
            return null;

        var full = Path.GetFullPath(Path.Combine(normalizedRoot, Path.Combine(segments)));
        return IsSameOrInside(full, normalizedRoot) ? full : null;
    }

    public static string Combine(string parentRelative, string name) =>
        parentRelative.Length == 0 ? name : parentRelative + "/" + name;

    public static bool IsSameOrInside(string candidate, string root)
    {
        candidate = Path.TrimEndingDirectorySeparator(candidate);
        root = Path.TrimEndingDirectorySeparator(root);
        if (candidate.Equals(root, PathComparison))
            return true;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, PathComparison);
    }
}
