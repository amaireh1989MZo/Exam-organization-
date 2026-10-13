using System.Security.Cryptography;
using System.Text;

namespace QuestionBank.Core.Quality;

/// <summary>
/// تطبيع آمن للمقارنة فقط: لا يغيّر النص المحفوظ أبدًا. يزيل الفروق الشكلية التي لا تغيّر السؤال:
/// التشكيل، والتطويل، وصور الألف والياء والتاء المربوطة، والأرقام العربية، وعلامات الترقيم، والمسافات.
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var raw in text.Normalize(NormalizationForm.FormKC))
        {
            var c = raw;
            if (c is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ')
                continue; // تشكيل وتطويل
            c = c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                _ => char.ToLowerInvariant(c)
            };
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                continue;
            }
            sb.Append(c);
            lastWasSpace = false;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>بصمة نص السؤال وحده.</summary>
    public static string StemHash(string text) => Hash(Normalize(text));

    /// <summary>بصمة السؤال كاملًا: النص ومجموعة الخيارات بغض النظر عن ترتيبها.</summary>
    public static string FullHash(string text, IEnumerable<string> options) =>
        Hash(Normalize(text) + "\u001F" + string.Join("\u001E", options.Select(Normalize).Order(StringComparer.Ordinal)));

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32];
}
