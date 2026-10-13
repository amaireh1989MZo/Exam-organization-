using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QuestionBank.Tests.Web;

/// <summary>معيار المرحلة 0: لا توجد نقطة بلا حماية إلا /health، والصلاحيات تتبع مجموعات AD.</summary>
public sealed class SecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qb-web-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var f in _factories)
            f.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// يشغّل التطبيق على Kestrel حقيقي على منفذ محلي عشوائي، لأن معالج Negotiate لا يعمل على TestServer.
    /// </summary>
    private HttpClient Client(string environment = "Testing", bool testAuth = false,
        Dictionary<string, string?>? settings = null)
    {
        var factory = Factory(environment, testAuth, settings);
        factory.UseKestrel(0);
        factory.StartServer();
        _factories.Add(factory);
        return factory.CreateClient();
    }

    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private WebApplicationFactory<Program> Factory(string environment = "Testing", bool testAuth = false,
        Dictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("ConnectionStrings:QuestionBank", $"Data Source={Path.Combine(_dir, "web.db")};Pooling=False");
            b.UseSetting("QuestionBank:EnableWorker", "false");
            b.UseSetting("QuestionBank:SourceRoot", Path.Combine(_dir, "src"));
            b.UseSetting("QuestionBank:OutputRoot", Path.Combine(_dir, "out"));
            foreach (var (k, v) in settings ?? [])
                b.UseSetting(k, v);
            if (testAuth)
            {
                b.ConfigureTestServices(s => s
                    .AddAuthentication(o =>
                    {
                        o.DefaultScheme = TestAuthHandler.Scheme;
                        o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                        o.DefaultChallengeScheme = TestAuthHandler.Scheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { }));
            }
        });

    [Fact]
    public async Task Health_IsAnonymous()
    {
        var client = Client();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/me")]
    [InlineData("GET", "/api/config/paths")]
    [InlineData("GET", "/api/inventory/status")]
    [InlineData("POST", "/api/inventory/scan")]
    [InlineData("POST", "/api/inventory/abandon")]
    [InlineData("GET", "/not-a-real-page")]
    public async Task EveryOtherEndpoint_RequiresWindowsAuthentication(string method, string url)
    {
        var client = Client();
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");
    }

    [Theory]
    [InlineData("CORP\\QB-Reviewers", "/api/inventory/status", HttpStatusCode.Forbidden)]
    [InlineData("CORP\\QB-BankManagers", "/api/inventory/status", HttpStatusCode.OK)]
    [InlineData("CORP\\QB-BankManagers", "/api/config/paths", HttpStatusCode.Forbidden)]
    [InlineData("CORP\\QB-Admins", "/api/config/paths", HttpStatusCode.OK)]
    [InlineData("CORP\\QB-Viewers", "/api/me", HttpStatusCode.OK)]
    [InlineData("CORP\\Some-Other-Group", "/api/me", HttpStatusCode.Forbidden)]
    public async Task Roles_FollowConfiguredAdGroups(string group, string url, HttpStatusCode expected)
    {
        var client = Client(testAuth: true);
        client.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, group);
        var response = await client.GetAsync(url);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ConfigPaths_NeverRevealsThePathsThemselves()
    {
        var client = Client(testAuth: true);
        client.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, "CORP\\QB-Admins");
        var body = await client.GetStringAsync("/api/config/paths");
        Assert.DoesNotContain(_dir, body);
        Assert.DoesNotContain("src", body);
    }

    [Fact]
    public void DevelopmentAuthMode_IsRefusedOutsideDevelopment()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            Client("Production", settings: new() { ["Authentication:Mode"] = "Development" }));
        Assert.Contains("Development", ex.ToString());
    }

    /// <summary>مستخدم اختبار مجموعاته من ترويسة الطلب. بدون ترويسة = زائر غير مصادَق.</summary>
    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "Test";
        public const string GroupsHeader = "X-Test-Groups";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(GroupsHeader, out var groups))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.Name, "CORP\\tester") };
            claims.AddRange(groups.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(g => new Claim(ClaimTypes.Role, g.Trim())));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
        }
    }
}
