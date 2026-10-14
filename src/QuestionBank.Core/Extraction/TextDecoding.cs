using System.Text;

namespace QuestionBank.Core.Extraction;

/// <summary>
/// فك ترميز ملفات النص القديمة: UTF-8 (مع BOM أو بدونه)، وUTF-16 بـ BOM،
/// وإلا Windows-1256 وهو الترميز العربي الشائع في ملفات Windows القديمة.
/// </summary>
public static class TextDecoding
{
    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "utf-8");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16le");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16be");

        try
        {
            return (StrictUtf8.GetString(bytes), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(1256).GetString(bytes), "windows-1256");
        }
    }

    /// <summary>يحوّل الأرقام العربية الهندية والفارسية إلى 0-9، لمطابقة أرقام الأسئلة فقط (لا يغيّر النص المحفوظ).</summary>
    public static string NormalizeDigits(string s)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= '٠' and <= '٩') chars[i] = (char)('0' + (c - '٠'));
            else if (c is >= '۰' and <= '۹') chars[i] = (char)('0' + (c - '۰'));
        }
        return new string(chars);
    }
}
