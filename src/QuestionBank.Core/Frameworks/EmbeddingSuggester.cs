using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QuestionBank.Core.Frameworks;

/// <summary>إعدادات الاقتراحات: QuestionBank:Ai.</summary>
public sealed class AiOptions
{
    public const string SectionName = "QuestionBank:Ai";

    /// <summary>lexical (الافتراضي، بلا نموذج) أو ollama (نموذج تضمين محلي).</summary>
    public string Provider { get; set; } = "lexical";

    /// <summary>عنوان Ollama على الخادم الداخلي.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "bge-m3";

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>أقل تشابه دلالي يُعرض. يُضبط بعد القياس على عينة المختصين.</summary>
    [Range(0.0, 1.0)]
    public double MinScore { get; set; } = 0.35;

    /// <summary>
    /// السماح بعنوان خارج الشبكة الداخلية. معطّل افتراضيًا: إرسال نصوص الأسئلة لخدمة خارجية يخالف شرط السرية.
    /// </summary>
    public bool AllowNonLocalEndpoint { get; set; }
}

/// <summary>
/// اقتراحات دلالية من نموذج تضمين محلي عبر Ollama (POST /api/embed).
/// تضمينات المؤشرات تُحفظ في الذاكرة (حسب نصها)، فلا تُعاد مع كل طلب. إذا تعذّر الوصول للنموذج
/// تُستخدم الاقتراحات النصية، ويظهر ذلك في طريقة الاقتراح ومبرره.
/// </summary>
public sealed class EmbeddingSuggester(
    HttpClient http,
    IOptions<AiOptions> options,
    LexicalSuggester fallback,
    ILogger<EmbeddingSuggester> logger) : IIndicatorSuggester
{
    private readonly ConcurrentDictionary<string, float[]> _indicatorCache = new();

    public string Method => "embedding";

    /// <summary>يتحقق أن العنوان داخلي (localhost أو شبكة خاصة أو اسم .local/بلا نطاق) ما لم يُسمح صراحة بغيره.</summary>
    public static string? ValidateEndpoint(AiOptions o)
    {
        if (!Uri.TryCreate(o.Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "عنوان نموذج الذكاء الاصطناعي غير صالح.";
        if (o.AllowNonLocalEndpoint || IsInternal(uri))
            return null;
        return "عنوان نموذج الذكاء الاصطناعي خارج الشبكة الداخلية؛ مرفوض حفاظًا على السرية (AllowNonLocalEndpoint=false).";
    }

    private static bool IsInternal(Uri uri)
    {
        if (uri.IsLoopback)
            return true;
        var host = uri.IdnHost;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || IPAddress.IsLoopback(ip);
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127;
        }
        // اسم مضيف بلا نطاق (AISERVER) أو نطاق داخلي شائع.
        return !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".corp", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<IReadOnlyList<IndicatorSuggestion>>> SuggestManyAsync(
        IReadOnlyList<(string Text, IReadOnlyList<string> Options)> questions, IReadOnlyList<CompetencyIndicator> indicators,
        int top, CancellationToken cancellationToken)
    {
        if (questions.Count == 0 || indicators.Count == 0)
            return questions.Select(_ => (IReadOnlyList<IndicatorSuggestion>)[]).ToList();

        var o = options.Value;
        var problem = ValidateEndpoint(o);
        if (problem is not null)
        {
            logger.LogWarning("Embedding endpoint rejected: {Reason}", "not internal or invalid");
            return await Fallback(questions, indicators, top, "النموذج المحلي غير مضبوط بعنوان داخلي", cancellationToken);
        }

        try
        {
            var indicatorTexts = indicators.Select(IndicatorText).ToList();
            var missing = indicatorTexts.Where(t => !_indicatorCache.ContainsKey(Key(t))).Distinct().ToList();
            if (missing.Count > 0)
            {
                var vectors = await EmbedAsync(missing, cancellationToken);
                for (var i = 0; i < missing.Count; i++)
                    _indicatorCache[Key(missing[i])] = vectors[i];
            }
            var indicatorVectors = indicatorTexts.Select(t => _indicatorCache[Key(t)]).ToList();
            // السؤال وحده (بلا الخيارات): الخيارات الخاطئة تشوّش المعنى.
            var questionVectors = await EmbedAsync(questions.Select(q => q.Text).ToList(), cancellationToken);

            return questionVectors.Select(qv => (IReadOnlyList<IndicatorSuggestion>)indicators
                    .Select((ind, i) => new IndicatorSuggestion(ind.Id, ind.Code, ind.Name, Math.Round(Cosine(qv, indicatorVectors[i]), 3),
                        Method, $"تشابه دلالي (نموذج {o.Model} المحلي)"))
                    .Where(s => s.Score >= o.MinScore)
                    .OrderByDescending(s => s.Score)
                    .Take(top)
                    .ToList())
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            // لا نسجل الرسالة: قد تحتوي نصًا. النوع يكفي.
            logger.LogWarning("Embedding model unavailable ({ExceptionType}); falling back to lexical suggestions.", ex.GetType().Name);
            return await Fallback(questions, indicators, top, "النموذج المحلي غير متاح", cancellationToken);
        }
    }

    private async Task<IReadOnlyList<IReadOnlyList<IndicatorSuggestion>>> Fallback(
        IReadOnlyList<(string Text, IReadOnlyList<string> Options)> questions, IReadOnlyList<CompetencyIndicator> indicators,
        int top, string reason, CancellationToken cancellationToken)
    {
        var lexical = await fallback.SuggestManyAsync(questions, indicators, top, cancellationToken);
        return lexical.Select(list => (IReadOnlyList<IndicatorSuggestion>)list
            .Select(s => s with { Rationale = $"{s.Rationale} · {reason}، استُخدمت المقارنة النصية" }).ToList()).ToList();
    }

    private async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var result = new List<float[]>(inputs.Count);
        // دفعات صغيرة حتى لا يطول الطلب الواحد.
        foreach (var chunk in inputs.Chunk(32))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
            var url = new Uri(new Uri(o.Endpoint.TrimEnd('/') + "/"), "api/embed");
            using var response = await http.PostAsJsonAsync(url, new EmbedRequest(o.Model, chunk), cts.Token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(cts.Token);
            if (body?.Embeddings is not { } embeddings || embeddings.Length != chunk.Length || embeddings.Any(e => e.Length == 0))
                throw new InvalidDataException("Embedding response does not match the request.");
            result.AddRange(embeddings);
        }
        return result;
    }

    private static string IndicatorText(CompetencyIndicator i) =>
        string.Join(" — ", new[] { i.Name, i.SubDomain, i.MainDomain }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private string Key(string text) =>
        options.Value.Model + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new InvalidDataException("Embedding dimensions differ.");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private sealed record EmbedRequest([property: JsonPropertyName("model")] string Model, [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record EmbedResponse([property: JsonPropertyName("embeddings")] float[][]? Embeddings);
}
