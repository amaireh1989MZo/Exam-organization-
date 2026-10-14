using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Features;
using QuestionBank.Core.Data;
using QuestionBank.Core.Export;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Frameworks;
using QuestionBank.Core.Quality;
using QuestionBank.Core.Reports;
using QuestionBank.Core.Review;
using QuestionBank.Core.Inventory;
using QuestionBank.Web.Security;
using QuestionBank.Core.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddQuestionBankCore(builder.Configuration);
// العامل داخل الموقع افتراضيًا. للجرد الكامل الطويل: EnableWorker=false هنا، وتشغيل خدمة QuestionBank.Worker.
if (builder.Configuration.GetValue("QuestionBank:EnableWorker", true))
    builder.Services.AddQuestionBankWorkers();

builder.Services.AddAppAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAppAuthorization(builder.Configuration);

// نص عربي مقروء في JSON بدل \uXXXX.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddProblemDetails();

var app = builder.Build();

await ServiceRegistration.InitializeDatabaseAsync(app.Services, app.Configuration);

if (!app.Environment.IsDevelopment())
{
    // لا تفاصيل أخطاء ولا stack traces للمستخدمين في الإنتاج.
    app.UseExceptionHandler();
    app.UseHsts();
    app.UseHttpsRedirection();
}

// ترويسات أمان لكل استجابة. CSP يمنع أي سكربت أو اتصال خارجي (الواجهة لا تستخدم أي مصدر خارجي).
// style-src يسمح بالأنماط المضمنة لأن الواجهة تضبط بعض الأبعاد ديناميكيًا؛ السكربتات تبقى من التطبيق فقط.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    await next();
});

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

inventory.MapGet("/status", async (InventoryQueries queries, WorkerCommandService workers, CancellationToken ct) =>
{
    var status = await queries.GetStatusAsync(ct);
    var worker = await workers.StatusAsync(WorkerCommandService.InventoryWorkerName, ct);
    var extraction = await workers.StatusAsync(WorkerCommandService.ExtractionWorkerName, ct);
    return Results.Ok(new
    {
        worker = new
        {
            running = worker.Running,
            scanning = worker.Busy,
            problemCode = worker.ProblemCode,
            problem = worker.Problem,
            heartbeatUtc = worker.HeartbeatUtc,
            host = worker.Host
        },
        extraction,
        status
    });
});

inventory.MapPost("/scan", async (HttpContext ctx, WorkerCommandService workers, WorkerSignals signals, CancellationToken ct) =>
{
    await workers.EnqueueAsync(WorkerCommandKinds.Scan, UserName(ctx), ct);
    signals.WakeInventory();
    return Results.Accepted("/api/inventory/status", new { message = "طُلب بدء الجرد أو استئنافه." });
});

inventory.MapPost("/abandon", async (HttpContext ctx, WorkerCommandService workers, WorkerSignals signals, CancellationToken ct) =>
{
    await workers.EnqueueAsync(WorkerCommandKinds.Abandon, UserName(ctx), ct);
    signals.WakeInventory();
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

queue.MapPost("/", async (EnqueueRequest request, HttpContext ctx, FolderService folders, WorkerSignals signals, CancellationToken ct) =>
{
    var (outcome, entry) = await folders.EnqueueAsync(request.Path ?? "", ctx.User.Identity?.Name ?? "unknown", ct);
    if (outcome == EnqueueOutcome.Added)
        signals.WakeExtraction();
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

uploads.MapGet("/{id:long}/preview", async (long id, UploadService service, CancellationToken ct) =>
{
    var stored = await service.StoredAsync(id, ct);
    if (stored is null || stored.Value.Upload.Extension != "xlsx")
        return Results.NotFound(new { message = "المعاينة متاحة لملفات Excel المرفوعة فقط." });
    try
    {
        return Results.Ok(XlsxReader.Preview(await File.ReadAllBytesAsync(stored.Value.Path, ct)));
    }
    catch (Exception ex) when (ex is FormatException or InvalidDataException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
    {
        return Results.BadRequest(new { message = "تعذّرت قراءة ملف Excel." });
    }
});

uploads.MapPost("/{id:long}/reextract", async (long id, UploadService service, CancellationToken ct) =>
{
    var view = await service.ReextractAsync(id, ct);
    return view is null ? Results.NotFound() : Results.Ok(view);
});

var templates = app.MapGroup("/api/templates").RequireAuthorization(AppPolicies.ManageBank);

templates.MapGet("/", async (ColumnTemplateService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

templates.MapPost("/", async (TemplateRequest request, HttpContext ctx, ColumnTemplateService service, WorkerSignals signals, CancellationToken ct) =>
{
    var (template, error) = await service.SaveAsync(request, UserName(ctx), ct);
    if (template is null)
        return Results.BadRequest(new { message = error });
    var requeued = await service.RequeueUnmappedAsync(UserName(ctx), ct);
    if (requeued > 0)
        signals.WakeExtraction();
    return Results.Ok(new { template, requeuedSourceFiles = requeued });
});

uploads.MapGet("/", async (UploadService service, ReaderRegistry readers, IOptions<UploadOptions> options, CancellationToken ct) =>
    Results.Ok(new
    {
        maxFileSizeMb = options.Value.MaxFileSizeMb,
        allowedExtensions = options.Value.AllowZip ? options.Value.Extensions.Append("zip").ToArray() : options.Value.Extensions.ToArray(),
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
    if (UploadService.IsArchive(file.FileName))
    {
        var archive = await service.SaveArchiveAsync(stream, file.FileName, request.HttpContext.User.Identity?.Name ?? "unknown", ct);
        return archive.ErrorCode is not null
            ? Results.BadRequest(new { code = archive.ErrorCode, message = archive.Message })
            : Results.Ok(new { archive = true, items = archive.Items, skipped = archive.Skipped });
    }
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
    var images = await QuestionImageQueries.RefsAsync(db, rows.Select(q => q.Id).ToList(), ct);
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
        reviewStatus = q.ReviewStatus.ToString(),
        images = images.GetValueOrDefault(q.Id)
    }));
}).RequireAuthorization(AppPolicies.Review);

// صور الأسئلة بالبصمة. تُعرض فقط الصيغ الآمنة التي يعرضها المتصفح (لا SVG ولا HTML)، مع nosniff من الترويسات العامة.
app.MapGet("/api/images/{sha}", async (string sha, QuestionBankDbContext db, CancellationToken ct) =>
{
    if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
        return Results.NotFound();
    var blob = await db.ImageBlobs.AsNoTracking().FirstOrDefaultAsync(b => b.Sha256 == sha.ToUpperInvariant(), ct);
    if (blob is null || !ExtractedImage.IsDisplayable(blob.ContentType))
        return Results.NotFound();
    return Results.File(blob.Content, blob.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{blob.Sha256}\""));
}).RequireAuthorization(AppPolicies.Review);

var review = app.MapGroup("/api/review").RequireAuthorization(AppPolicies.Review);

review.MapGet("/", async (string? status, int? skip, int? take, bool? mine, HttpContext ctx, ReviewService service, CancellationToken ct) =>
{
    QuestionReviewStatus? filter = null;
    if (!string.IsNullOrEmpty(status))
    {
        if (!Enum.TryParse<QuestionReviewStatus>(status, out var parsed))
            return Results.BadRequest(new { message = "حالة غير معروفة." });
        filter = parsed;
    }
    return Results.Ok(await service.ListAsync(filter, skip ?? 0, take ?? 100, ct, mine == true ? UserName(ctx) : null));
});

review.MapGet("/assignments", async (ReviewService service, CancellationToken ct) => Results.Ok(await service.ProgressAsync(ct)))
    .RequireAuthorization(AppPolicies.ManageBank);
review.MapPost("/assignments", async (AssignmentRequest request, HttpContext ctx, ReviewService service, CancellationToken ct) =>
    Results.Ok(new { assigned = await service.AssignAsync(request, UserName(ctx), ct) }))
    .RequireAuthorization(AppPolicies.ManageBank);
review.MapPost("/assignments/unassign", async (UnassignRequest request, HttpContext ctx, ReviewService service, CancellationToken ct) =>
    Results.Ok(new { unassigned = await service.UnassignOpenAsync(request.Reviewer ?? "", UserName(ctx), ct) }))
    .RequireAuthorization(AppPolicies.ManageBank);

review.MapGet("/{id:long}/history", async (long id, ReviewService service, CancellationToken ct) =>
    Results.Ok(await service.HistoryAsync(id, ct)));

review.MapPost("/{id:long}/answer", async (long id, AnswerRequest request, HttpContext ctx, ReviewService service, CancellationToken ct) =>
    ReviewResult(await service.SetAnswerAsync(id, request.Index, UserName(ctx), ct)));
review.MapPost("/{id:long}/text", async (long id, TextRequest request, HttpContext ctx, ReviewService service, CancellationToken ct) =>
    ReviewResult(await service.SetTextAsync(id, request.Text, request.Options, UserName(ctx), ct)));
review.MapPost("/{id:long}/approve", async (long id, HttpContext ctx, ReviewService service, CancellationToken ct) =>
    ReviewResult(await service.ApproveAsync(id, UserName(ctx), ct)));
review.MapPost("/{id:long}/flag", async (long id, HttpContext ctx, ReviewService service, CancellationToken ct) =>
    ReviewResult(await service.FlagAsync(id, UserName(ctx), ct)));
review.MapPost("/{id:long}/reopen", async (long id, HttpContext ctx, ReviewService service, CancellationToken ct) =>
    ReviewResult(await service.ReopenAsync(id, UserName(ctx), ct)));

app.MapGet("/api/quality/duplicates", async (string? kind, int? skip, int? take, DuplicateService service, CancellationToken ct) =>
{
    var k = DuplicateKind.Exact;
    if (!string.IsNullOrEmpty(kind) && !Enum.TryParse(kind, ignoreCase: true, out k))
        return Results.BadRequest(new { message = "النوع يجب أن يكون Exact أو SameStem أو Near." });
    if (k == DuplicateKind.Near)
    {
        var counts = await service.GetGroupsAsync(DuplicateKind.Exact, 0, 1, ct);
        var (nearCount, nearGroups) = await service.GetNearGroupsAsync(skip ?? 0, take ?? 50, ct);
        return Results.Ok(new DuplicatePage(counts.ExactGroups, counts.SameStemGroups, nearCount, nearGroups, nearCount));
    }
    return Results.Ok(await service.GetGroupsAsync(k, skip ?? 0, take ?? 50, ct));
}).RequireAuthorization(AppPolicies.Review);

var frameworks = app.MapGroup("/api/frameworks").RequireAuthorization(AppPolicies.Review);

frameworks.MapGet("/", async (FrameworkService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

frameworks.MapGet("/{id:long}/tree", async (long id, FrameworkService service, CancellationToken ct) =>
    await service.TreeAsync(id, ct) is { } tree ? Results.Ok(tree) : Results.NotFound());

frameworks.MapGet("/{id:long}/unclassified", async (long id, int? take, FrameworkService service, CancellationToken ct) =>
    Results.Ok(await service.UnclassifiedAsync(id, take ?? 50, ct)));

// استيراد الإطار لمدير النظام ومسؤول البنك فقط.
frameworks.MapPost("/", async (HttpRequest request, FrameworkService service, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { errors = new[] { "أرسل ملف الإطار بصيغة multipart/form-data." } });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || !file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || file.Length > 20 * 1024 * 1024)
        return Results.BadRequest(new { errors = new[] { "أرسل ملف إطار بصيغة XLSX حتى 20 ميغابايت." } });
    using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    var result = await service.ImportAsync(ms.ToArray(), file.FileName, form["name"].ToString(), form["version"].ToString(),
        request.HttpContext.User.Identity?.Name ?? "unknown", ct);
    return result.Framework is null ? Results.BadRequest(result) : Results.Ok(result);
}).RequireAuthorization(AppPolicies.ManageBank);

app.MapPost("/api/classify/{questionId:long}", async (long questionId, ClassifyRequest request, HttpContext ctx, FrameworkService service, CancellationToken ct) =>
    await service.AssignAsync(questionId, request.IndicatorId, request.Method ?? "manual", request.Score, UserName(ctx), ct) switch
    {
        ClassifyOutcome.Ok => Results.NoContent(),
        ClassifyOutcome.IndicatorNotFound => Results.BadRequest(new { message = "المؤشر غير موجود في أي إطار مستورد." }),
        _ => Results.NotFound()
    }).RequireAuthorization(AppPolicies.Review);

var reports = app.MapGroup("/api/reports").RequireAuthorization(AppPolicies.ManageBank);

reports.MapPost("/excel", async (HttpContext ctx, ExcelReportService service, CancellationToken ct) =>
{
    var result = await service.GenerateAsync(UserName(ctx), ct);
    return result is null ? Results.Problem("مجلد النتائج غير مضبوط.", statusCode: 500) : Results.Ok(result);
});

reports.MapGet("/{fileName}", (string fileName, ExcelReportService service) =>
{
    var path = service.PathOf(fileName);
    return path is null
        ? Results.NotFound()
        : Results.File(path, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

var exports = app.MapGroup("/api/exports").RequireAuthorization(AppPolicies.ManageBank);

exports.MapGet("/", async (ExportService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

exports.MapPost("/", async (ExportRequestDto request, HttpContext ctx, ExportService service, CancellationToken ct) =>
{
    if (!Enum.TryParse<ExportFormat>(request.Format, ignoreCase: true, out var format))
        return Results.BadRequest(new { message = "الصيغة يجب أن تكون Gift أو MoodleXml." });
    var result = await service.ExportAsync(
        new ExportRequest(format, request.QuestionIds ?? [], request.AllowShuffle, request.CategoryBySource), UserName(ctx), ct);
    return result.ErrorCode switch
    {
        null => Results.Ok(result),
        "VerificationFailed" => Results.Problem("فشل الفحص الذاتي للملف قبل حفظه، ولم يُحفظ.", statusCode: 500),
        "OutputUnavailable" => Results.Problem("مجلد النتائج غير مضبوط.", statusCode: 500),
        _ => Results.BadRequest(new
        {
            code = result.ErrorCode,
            message = result.ErrorCode switch
            {
                "Empty" => "السلة فارغة.",
                "TooMany" => $"الحد الأقصى {ExportService.MaxQuestionsPerExport} سؤال في الملف الواحد.",
                _ => "لا يوجد في السلة سؤال صالح للتصدير."
            },
            rejected = result.Rejected
        })
    };
});

exports.MapGet("/{id:long}/file", async (long id, ExportService service, CancellationToken ct) =>
{
    var file = await service.GetFileAsync(id, ct);
    return file is null ? Results.NotFound() : Results.File(file.Value.Path, file.Value.ContentType, file.Value.FileName);
});

app.Run();

static string UserName(HttpContext ctx) => ctx.User.Identity?.Name ?? "unknown";

static IResult ReviewResult(ReviewOutcome outcome) => outcome switch
{
    ReviewOutcome.Ok => Results.NoContent(),
    ReviewOutcome.NotFound => Results.NotFound(),
    ReviewOutcome.InvalidOption => Results.BadRequest(new { message = "رقم الخيار غير صحيح." }),
    ReviewOutcome.NoAnswer => Results.BadRequest(new { message = "حدّد الإجابة الصحيحة قبل الاعتماد." }),
    ReviewOutcome.InvalidText => Results.BadRequest(new { message = "النص والخيارات لا يجوز أن تكون فارغة، وعدد الخيارات ثابت." }),
    _ => Results.Conflict(new { message = "أعد فتح السؤال المعتمد قبل تعديل إجابته." })
};

public partial class Program;

public sealed record EnqueueRequest(string? Path);

public sealed record AnswerRequest(int? Index);

public sealed record TextRequest(string? Text, string[]? Options);

public sealed record UnassignRequest(string? Reviewer);

public sealed record ClassifyRequest(long? IndicatorId, string? Method, double? Score);

public sealed record ExportRequestDto(string Format, long[]? QuestionIds, bool AllowShuffle, bool CategoryBySource);

/// <summary>الترويسة المطلوبة في كل طلب يغيّر شيئًا (حماية CSRF).</summary>
public static class ClientHeader
{
    public const string Name = "X-QB-Client";
    public const string Value = "web";
}
