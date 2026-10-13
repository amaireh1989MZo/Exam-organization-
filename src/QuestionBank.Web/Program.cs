using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Features;
using QuestionBank.Core.Data;
using QuestionBank.Core.Extraction;
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
builder.Services.AddScoped<FolderService>();
builder.Services.AddOptions<UploadOptions>()
    .Bind(builder.Configuration.GetSection(UploadOptions.SectionName))
    .ValidateDataAnnotations();
builder.Services.AddSingleton(ReaderRegistry.Default());
builder.Services.AddScoped<ExtractionService>();
builder.Services.AddScoped<UploadService>();
builder.Services.AddScoped<QueueProcessor>();
builder.Services.AddSingleton<ExtractionSignal>();
builder.Services.AddSingleton<InventoryCoordinator>();
if (builder.Configuration.GetValue("QuestionBank:EnableWorker", true))
{
    builder.Services.AddHostedService<InventoryWorker>();
    builder.Services.AddHostedService<ExtractionWorker>();
}

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

// حماية من CSRF: مع Windows Auth يرسل المتصفح بيانات الدخول تلقائيًا، فقد تستطيع صفحة خارجية إرسال POST
// باسم المستخدم. كل طلب يغيّر شيئًا يجب أن يحمل ترويسة مخصصة، والمتصفح لا يسمح لصفحة خارجية بإضافتها
// دون إذن CORS، والتطبيق لا يمنح أي إذن CORS.
app.Use(async (ctx, next) =>
{
    var changesState = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
                       && !HttpMethods.IsOptions(ctx.Request.Method);
    if (changesState && ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers[ClientHeader.Name] != ClientHeader.Value)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { message = "طلب غير مقبول: يجب إرساله من واجهة التطبيق." });
        return;
    }
    await next();
});

// الواجهة بعد التحقق من الصلاحية، فتنطبق عليها السياسة الافتراضية: لا يراها إلا من له دور.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-store"
});

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

inventory.MapGet("/tree", async (string? path, FolderService folders, CancellationToken ct) =>
{
    var listing = await folders.ListAsync(path ?? "", ct);
    return listing is null
        ? Results.NotFound(new { message = "المجلد غير موجود في الجرد." })
        : Results.Ok(listing);
});

var queue = app.MapGroup("/api/queue").RequireAuthorization(AppPolicies.ManageBank);

queue.MapGet("/", async (FolderService folders, CancellationToken ct) => Results.Ok(await folders.GetQueueAsync(ct)));

queue.MapPost("/", async (EnqueueRequest request, HttpContext ctx, FolderService folders, ExtractionSignal signal, CancellationToken ct) =>
{
    var (outcome, entry) = await folders.EnqueueAsync(request.Path ?? "", ctx.User.Identity?.Name ?? "unknown", ct);
    if (outcome == EnqueueOutcome.Added)
        signal.Notify();
    return outcome switch
    {
        EnqueueOutcome.Added => Results.Created($"/api/queue/{entry!.Id}", entry),
        EnqueueOutcome.AlreadyQueued => Results.Ok(entry),
        EnqueueOutcome.InvalidPath => Results.BadRequest(new { message = "مسار المجلد غير صالح." }),
        _ => Results.NotFound(new { message = "المجلد غير موجود في الجرد أو لا يحتوي ملفات." })
    };
});

queue.MapDelete("/{id:long}", async (long id, FolderService folders, CancellationToken ct) =>
    await folders.CancelAsync(id, ct) ? Results.NoContent() : Results.NotFound());

var uploads = app.MapGroup("/api/uploads").RequireAuthorization(AppPolicies.ManageBank);

uploads.MapGet("/", async (UploadService service, ReaderRegistry readers, IOptions<UploadOptions> options, CancellationToken ct) =>
    Results.Ok(new
    {
        maxFileSizeMb = options.Value.MaxFileSizeMb,
        allowedExtensions = options.Value.AllowedExtensions,
        readableExtensions = readers.SupportedExtensions,
        items = await service.ListAsync(200, ct)
    }));

// ملف واحد في كل طلب: الواجهة ترفع الملفات واحدًا تلو الآخر وتعرض تقدم كل ملف.
uploads.MapPost("/", async (HttpRequest request, UploadService service, IOptions<UploadOptions> options, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "أرسل الملف بصيغة multipart/form-data." });
    var form = await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = options.Value.MaxBytes + 1024 * 1024 }, ct);
    var file = form.Files.GetFile("file");
    if (file is null)
        return Results.BadRequest(new { message = "لم يُرسل أي ملف." });

    await using var stream = file.OpenReadStream();
    var result = await service.SaveAsync(stream, file.FileName, form["path"].FirstOrDefault(),
        request.HttpContext.User.Identity?.Name ?? "unknown", ct);
    if (result.ErrorCode is not null)
        return Results.BadRequest(new { code = result.ErrorCode, message = result.Message });
    return Results.Ok(new { duplicate = result.Duplicate, message = result.Message, upload = result.Upload });
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2L * 1024 * 1024 * 1024));

app.MapGet("/api/questions", async (long? uploadId, long? sourceFileId, QuestionBankDbContext db, CancellationToken ct) =>
{
    if ((uploadId is null) == (sourceFileId is null))
        return Results.BadRequest(new { message = "حدد uploadId أو sourceFileId." });
    var rows = await db.Questions.AsNoTracking()
        .Where(q => uploadId != null ? q.UploadId == uploadId : q.SourceFileId == sourceFileId)
        .OrderBy(q => q.OriginalOrder)
        .Take(1000)
        .ToListAsync(ct);
    return Results.Ok(rows.Select(q => new
    {
        q.Id,
        q.StableKey,
        order = q.OriginalOrder,
        code = q.OriginalCode,
        text = q.OriginalText,
        options = System.Text.Json.JsonSerializer.Deserialize<string[]>(q.OriginalOptionsJson),
        correctIndex = q.OriginalCorrectIndex,
        detector = q.AnswerDetector,
        confidence = q.Confidence.ToString(),
        location = q.Location,
        issues = q.IssuesJson is null ? [] : System.Text.Json.JsonSerializer.Deserialize<string[]>(q.IssuesJson),
        reviewStatus = q.ReviewStatus.ToString()
    }));
}).RequireAuthorization(AppPolicies.Review);

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

public sealed record EnqueueRequest(string? Path);

/// <summary>الترويسة المطلوبة في كل طلب يغيّر شيئًا (حماية CSRF).</summary>
public static class ClientHeader
{
    public const string Name = "X-QB-Client";
    public const string Value = "web";
}
