using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestionBank.Core.Data;
using QuestionBank.Core.Inventory;
using QuestionBank.Web.Security;
using QuestionBank.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<InventoryOptions>()
    .Bind(builder.Configuration.GetSection(InventoryOptions.SectionName))
    .ValidateDataAnnotations();

// SQLite للتجربة. ملف القاعدة يجب أن يكون على قرص الخادم المحلي، وليس على مشاركة شبكة.
var connectionString = builder.Configuration.GetConnectionString("QuestionBank")
    ?? "Data Source=App_Data/questionbank.db";
builder.Services.AddDbContext<QuestionBankDbContext>(o => o.UseSqlite(connectionString));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISourceFileSystem, PhysicalSourceFileSystem>();
builder.Services.AddScoped<InventoryScanner>();
builder.Services.AddScoped<InventoryQueries>();
builder.Services.AddSingleton<InventoryCoordinator>();
if (builder.Configuration.GetValue("QuestionBank:EnableWorker", true))
    builder.Services.AddHostedService<InventoryWorker>();

builder.Services.AddAppAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAppAuthorization(builder.Configuration);

// نص عربي مقروء في JSON بدل \uXXXX.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All));
builder.Services.AddProblemDetails();

var app = builder.Build();

await InitializeDatabaseAsync(app.Services, connectionString);

if (!app.Environment.IsDevelopment())
{
    // لا تفاصيل أخطاء ولا stack traces للمستخدمين في الإنتاج.
    app.UseExceptionHandler();
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.MapGet("/api/me", (HttpContext ctx, IOptions<RoleMappingOptions> mapping) => Results.Ok(new
{
    name = ctx.User.Identity?.Name,
    roles = AppAuthorizationExtensions.RolesOf(ctx.User, mapping.Value)
}));

// يعرض هل المسارات مضبوطة ومتاحة فقط، ولا يعرض المسارات نفسها.
app.MapGet("/api/config/paths", (IOptions<InventoryOptions> options, ISourceFileSystem fs) =>
{
    var o = options.Value;
    return Results.Ok(new
    {
        sourceConfigured = !string.IsNullOrWhiteSpace(o.SourceRoot),
        outputConfigured = !string.IsNullOrWhiteSpace(o.OutputRoot),
        sourceExists = !string.IsNullOrWhiteSpace(o.SourceRoot) && fs.DirectoryExists(o.SourceRoot),
        outputExists = !string.IsNullOrWhiteSpace(o.OutputRoot) && Directory.Exists(o.OutputRoot),
        problems = SourcePaths.Validate(o, fs.DirectoryExists),
        batchSize = o.BatchSize,
        maxRetries = o.MaxRetries
    });
}).RequireAuthorization(AppPolicies.Admin);

var inventory = app.MapGroup("/api/inventory").RequireAuthorization(AppPolicies.ManageBank);

inventory.MapGet("/status", async (InventoryQueries queries, InventoryCoordinator coordinator, CancellationToken ct) =>
{
    var status = await queries.GetStatusAsync(ct);
    return Results.Ok(new
    {
        worker = new
        {
            scanning = coordinator.IsScanning,
            problemCode = coordinator.LastProblemCode,
            problem = coordinator.LastProblemMessage
        },
        status
    });
});

inventory.MapPost("/scan", (InventoryCoordinator coordinator) =>
{
    coordinator.RequestScan();
    return Results.Accepted("/api/inventory/status", new { message = "طُلب بدء الجرد أو استئنافه." });
});

inventory.MapPost("/abandon", (InventoryCoordinator coordinator) =>
{
    coordinator.RequestAbandon();
    return Results.Accepted("/api/inventory/status", new { message = "طُلب إيقاف عملية الجرد الحالية نهائيًا." });
});

app.Run();

static async Task InitializeDatabaseAsync(IServiceProvider services, string connectionString)
{
    var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
    var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<QuestionBankDbContext>();
    await db.Database.MigrateAsync();
    // WAL: قراءة لوحة الحالة لا تنتظر كتابات الجرد.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

public partial class Program;
