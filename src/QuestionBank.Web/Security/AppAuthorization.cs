using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace QuestionBank.Web.Security;

/// <summary>أدوار التطبيق. كل دور يُربط بمجموعة أو أكثر من مجموعات Active Directory في الإعدادات.</summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string BankManager = "BankManager";
    public const string Reviewer = "Reviewer";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, BankManager, Reviewer, Viewer];
}

public static class AppPolicies
{
    /// <summary>مدير النظام فقط.</summary>
    public const string Admin = "Admin";

    /// <summary>مدير النظام أو مسؤول بنك الأسئلة.</summary>
    public const string ManageBank = "ManageBank";

    /// <summary>مدير النظام أو مسؤول البنك أو المراجع الفني: الاطلاع على الأسئلة المستخرجة ومراجعتها.</summary>
    public const string Review = "Review";

    /// <summary>أي دور من أدوار التطبيق. هذه السياسة الافتراضية لكل نقطة لم تُحدَّد لها سياسة.</summary>
    public const string AnyRole = "AnyRole";
}

/// <summary>قسم Authorization:Roles في الإعدادات: اسم الدور ← أسماء مجموعات AD، مثل "CORP\\QB-Admins".</summary>
public sealed class RoleMappingOptions
{
    public Dictionary<string, string[]> Roles { get; set; } = new();

    public string[] GroupsFor(params string[] roles) =>
        roles.SelectMany(r => Roles.TryGetValue(r, out var groups) ? groups : []).Distinct().ToArray();
}

public static class AppAuthorizationExtensions
{
    public const string DevelopmentScheme = "Development";

    /// <summary>
    /// Authentication:Mode = Windows (الافتراضي، Negotiate/Kerberos عبر IIS أو Kestrel)
    /// أو Development (مستخدم ثابت بكل الأدوار، ويُرفض خارج بيئة Development).
    /// </summary>
    public static IServiceCollection AddAppAuthentication(
        this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var mode = config["Authentication:Mode"] ?? "Windows";
        if (mode.Equals(DevelopmentScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("Authentication:Mode=Development مسموح في بيئة Development فقط.");
            services.AddAuthentication(DevelopmentScheme)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(DevelopmentScheme, _ => { });
        }
        else
        {
            services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
        }
        return services;
    }

    public static IServiceCollection AddAppAuthorization(this IServiceCollection services, IConfiguration config)
    {
        var mapping = config.GetSection("Authorization").Get<RoleMappingOptions>() ?? new RoleMappingOptions();
        services.Configure<RoleMappingOptions>(config.GetSection("Authorization"));

        services.AddAuthorizationBuilder()
            .AddPolicy(AppPolicies.Admin, p => RequireAnyGroup(p, mapping.GroupsFor(AppRoles.Admin)))
            .AddPolicy(AppPolicies.ManageBank, p => RequireAnyGroup(p, mapping.GroupsFor(AppRoles.Admin, AppRoles.BankManager)))
            .AddPolicy(AppPolicies.Review, p => RequireAnyGroup(p, mapping.GroupsFor(AppRoles.Admin, AppRoles.BankManager, AppRoles.Reviewer)))
            .AddPolicy(AppPolicies.AnyRole, p => RequireAnyGroup(p, mapping.GroupsFor(AppRoles.All)))
            // لا توجد نقطة بلا حماية إلا ما يُعلَّم صراحة AllowAnonymous (مثل /health).
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => mapping.GroupsFor(AppRoles.All).Any(ctx.User.IsInRole))
                .Build());
        return services;
    }

    private static void RequireAnyGroup(AuthorizationPolicyBuilder policy, string[] groups) =>
        policy.RequireAuthenticatedUser()
              // إذا لم تُضبط أي مجموعة للدور فلا أحد يمر: الإعداد الناقص يغلق ولا يفتح.
              .RequireAssertion(ctx => groups.Length > 0 && groups.Any(ctx.User.IsInRole));

    /// <summary>أدوار المستخدم الحالي حسب ربط المجموعات، للواجهة.</summary>
    public static string[] RolesOf(ClaimsPrincipal user, RoleMappingOptions mapping) =>
        AppRoles.All.Where(r => mapping.GroupsFor(r).Any(user.IsInRole)).ToArray();
}

/// <summary>مستخدم تطوير ثابت يحمل كل المجموعات المضبوطة. يعمل في بيئة Development فقط.</summary>
public sealed class DevelopmentAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<RoleMappingOptions> mapping) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "DEV\\developer") };
        claims.AddRange(mapping.Value.GroupsFor(AppRoles.All).Select(g => new Claim(ClaimTypes.Role, g)));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
