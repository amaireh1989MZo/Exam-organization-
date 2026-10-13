using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuestionBank.Core.Frameworks;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace QuestionBank.Tests.Frameworks;

/// <summary>
/// خادم Ollama محاكى: يعيد تضمينًا بسيطًا من 3 أبعاد حسب كلمات مفتاحية، فنعرف مسبقًا أي مؤشر يجب أن يكون الأقرب.
/// لا يغني عن التجربة مع نموذج حقيقي، لكنه يثبت البروتوكول والترتيب والتخزين والسقوط إلى البديل.
/// </summary>
public sealed class EmbeddingSuggesterTests
{
    private sealed class FakeOllama : HttpMessageHandler
    {
        public int Calls;
        public int Inputs;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool WrongCount;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("/api/embed", request.RequestUri!.AbsolutePath);
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(LastBody);
            Assert.Equal("bge-m3", doc.RootElement.GetProperty("model").GetString());
            var inputs = doc.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
            Inputs += inputs.Count;
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status);
            var vectors = inputs.Select(Vector).ToList();
            if (WrongCount) vectors.RemoveAt(0);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { embeddings = vectors }), Encoding.UTF8, "application/json")
            };
        }

        // البعد 0: التخطيط، 1: التواصل، 2: الجغرافيا. "عواصم" و"عاصمة" تقعان في البعد نفسه (فهم دلالي محاكى).
        private static float[] Vector(string text) => new[]
        {
            text.Contains("المهام") || text.Contains("تعارض") ? 1f : 0.05f,
            text.Contains("يقوله") || text.Contains("الإصغاء") ? 1f : 0.05f,
            text.Contains("عواصم") || text.Contains("عاصمة") ? 1f : 0.05f
        };
    }

    private static readonly CompetencyIndicator[] Indicators =
    [
        new() { Id = 1, MainDomain = "التخطيط", Code = "1.1.2", Name = "يوازن بين المهام المتعارضة", SourceRow = "" },
        new() { Id = 2, MainDomain = "التواصل", Code = "2.2.1", Name = "يتحقق من فهمه لما يقوله الآخرون", SourceRow = "" },
        new() { Id = 3, MainDomain = "المعرفة", Code = "3.1.1", Name = "يعرف عواصم الدول", SourceRow = "" }
    ];

    private static (EmbeddingSuggester Suggester, FakeOllama Server) Create(string endpoint = "http://localhost:11434", bool allowNonLocal = false)
    {
        var server = new FakeOllama();
        var options = new AiOptions { Provider = "ollama", Endpoint = endpoint, AllowNonLocalEndpoint = allowNonLocal, MinScore = 0.5 };
        return (new EmbeddingSuggester(new HttpClient(server), MsOptions.Create(options), new LexicalSuggester(),
            NullLogger<EmbeddingSuggester>.Instance), server);
    }

    private static IReadOnlyList<(string, IReadOnlyList<string>)> Questions(params string[] texts) =>
        texts.Select(t => (t, (IReadOnlyList<string>)["أ", "ب"])).ToList();

    [Fact]
    public async Task RanksBySemanticSimilarity_AndUsesTheEmbedProtocol()
    {
        var (s, server) = Create();
        var result = await s.SuggestManyAsync(Questions("ما عاصمة الأردن؟", "عند تعارض المهام ماذا تفعل؟"), Indicators, 3, CancellationToken.None);

        Assert.Equal("3.1.1", result[0][0].Code);
        Assert.Equal("1.1.2", result[1][0].Code);
        Assert.All(result.SelectMany(r => r), x => Assert.Equal("embedding", x.Method));
        Assert.All(result.SelectMany(r => r), x => Assert.True(x.Score >= 0.5));
        Assert.Contains("bge-m3", result[0][0].Rationale);
    }

    [Fact]
    public async Task IndicatorEmbeddings_AreCached_AcrossRequests()
    {
        var (s, server) = Create();
        await s.SuggestManyAsync(Questions("ما عاصمة الأردن؟"), Indicators, 3, CancellationToken.None);
        var afterFirst = server.Inputs;
        await s.SuggestManyAsync(Questions("ما عاصمة مصر؟"), Indicators, 3, CancellationToken.None);

        Assert.Equal(4, afterFirst);           // 3 مؤشرات + سؤال
        Assert.Equal(5, server.Inputs);        // السؤال الثاني فقط
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task ServerErrorOrMalformedResponse_FallsBackToLexical_AndSaysSo(HttpStatusCode status, bool wrongCount)
    {
        var (s, server) = Create();
        server.Status = status;
        server.WrongCount = wrongCount;

        var result = await s.SuggestManyAsync(Questions("عند تعارض المهام المتعارضة ماذا تفعل؟"), Indicators, 3, CancellationToken.None);

        var top = Assert.Single(result)[0];
        Assert.Equal("lexical", top.Method);
        Assert.Contains("النموذج المحلي غير متاح", top.Rationale);
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://10.1.2.3:11434", true)]
    [InlineData("http://192.168.0.5:11434", true)]
    [InlineData("http://172.20.0.1:11434", true)]
    [InlineData("http://aiserver:11434", true)]
    [InlineData("https://ai.corp.local", true)]
    [InlineData("https://api.example.com", false)]
    [InlineData("http://8.8.8.8:11434", false)]
    [InlineData("http://172.32.0.1:11434", false)]
    [InlineData("ftp://localhost", false)]
    public void OnlyInternalEndpoints_AreAllowedByDefault(string endpoint, bool allowed) =>
        Assert.Equal(allowed, EmbeddingSuggester.ValidateEndpoint(new AiOptions { Endpoint = endpoint }) is null);

    [Fact]
    public async Task ExternalEndpoint_IsNeverCalled_QuestionTextNeverLeaves()
    {
        var (s, server) = Create("https://api.example.com");
        var result = await s.SuggestManyAsync(Questions("عند تعارض المهام المتعارضة؟"), Indicators, 3, CancellationToken.None);

        Assert.Equal(0, server.Calls);
        Assert.Contains("عنوان داخلي", Assert.Single(result)[0].Rationale);
    }

    [Fact]
    public async Task ExternalEndpoint_IsCalled_OnlyWhenExplicitlyAllowed()
    {
        var (s, server) = Create("https://ai.example.com", allowNonLocal: true);
        await s.SuggestManyAsync(Questions("ما عاصمة الأردن؟"), Indicators, 3, CancellationToken.None);
        Assert.True(server.Calls > 0);
    }
}
