using System.Text;

namespace QuestionBank.Core.Quality;

/// <summary>
/// بصمات MinHash لكشف شبه التكرار (صياغة مختلفة قليلًا) دون مقارنة كل سؤال بكل سؤال.
/// النص يُطبَّع ثم يُقسَّم إلى مقاطع من 3 أحرف (تناسب صرف العربية أكثر من الكلمات)، وتُحسب 64 قيمة MinHash
/// تُجمع في 16 حزمة من 4 قيم. سؤالان يشتركان في حزمة واحدة على الأقل مرشحان، ثم يُحسب تشابههما الفعلي.
/// مع هذه الأرقام: تشابه 0.8 يُكتشف باحتمال يقارب 99.9%، وتشابه 0.4 نادرًا ما يصبح مرشحًا.
/// </summary>
public static class NearDuplicates
{
    public const int Bands = 16;
    public const int Rows = 4;
    private const int HashCount = Bands * Rows;
    private const int ShingleSize = 3;
    private const int MinNormalizedLength = 12;
    private const ulong Prime = 4294967311UL; // أول عدد أولي بعد 2^32

    private static readonly (ulong A, ulong B)[] Coefficients = BuildCoefficients();

    /// <summary>مفاتيح الحزم للسؤال، أو قائمة فارغة إذا كان النص أقصر من أن يُقارن بثقة.</summary>
    public static IReadOnlyList<string> BandKeys(string text)
    {
        var shingles = Shingles(text);
        if (shingles.Count == 0)
            return [];

        var signature = new uint[HashCount];
        Array.Fill(signature, uint.MaxValue);
        foreach (var s in shingles)
        {
            for (var i = 0; i < HashCount; i++)
            {
                var h = (uint)((Coefficients[i].A * s + Coefficients[i].B) % Prime);
                if (h < signature[i])
                    signature[i] = h;
            }
        }

        var keys = new string[Bands];
        for (var b = 0; b < Bands; b++)
        {
            ulong acc = 1469598103934665603UL;
            for (var r = 0; r < Rows; r++)
                acc = (acc ^ signature[b * Rows + r]) * 1099511628211UL;
            keys[b] = acc.ToString("x16");
        }
        return keys;
    }

    /// <summary>تشابه Jaccard الفعلي بين نصين على مستوى المقاطع (0 إلى 1).</summary>
    public static double Similarity(string a, string b)
    {
        var sa = Shingles(a);
        var sb = Shingles(b);
        if (sa.Count == 0 || sb.Count == 0)
            return 0;
        var intersection = sa.Count(sb.Contains);
        return intersection / (double)(sa.Count + sb.Count - intersection);
    }

    public static HashSet<uint> Shingles(string text)
    {
        var n = TextNormalizer.Normalize(text);
        var set = new HashSet<uint>();
        if (n.Length < MinNormalizedLength)
            return set;
        for (var i = 0; i + ShingleSize <= n.Length; i++)
            set.Add(Fnv1a(n.AsSpan(i, ShingleSize)));
        return set;
    }

    private static uint Fnv1a(ReadOnlySpan<char> s)
    {
        var h = 2166136261u;
        foreach (var c in s)
        {
            h ^= c;
            h *= 16777619u;
        }
        return h;
    }

    /// <summary>معاملات ثابتة (ليست عشوائية في كل تشغيل) حتى تبقى البصمات المحفوظة صالحة بين التشغيلات.</summary>
    private static (ulong, ulong)[] BuildCoefficients()
    {
        var result = new (ulong, ulong)[HashCount];
        ulong state = 0x9E3779B97F4A7C15UL;
        ulong Next()
        {
            state ^= state << 13; state ^= state >> 7; state ^= state << 17;
            return state;
        }
        for (var i = 0; i < HashCount; i++)
            result[i] = (Next() % (Prime - 1) + 1, Next() % Prime);
        return result;
    }
}

/// <summary>مفتاح حزمة MinHash لسؤال. يُحذف مع السؤال.</summary>
public sealed class QuestionBand
{
    public long Id { get; set; }
    public long QuestionId { get; set; }
    public byte Band { get; set; }
    public required string Key { get; set; }
}
