using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QuestionBank.Core.Data;
using QuestionBank.Core.Export;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Frameworks;
using QuestionBank.Core.Inventory;
using QuestionBank.Core.Quality;
using QuestionBank.Core.Reports;
using QuestionBank.Core.Review;

namespace QuestionBank.Core.Hosting;

/// <summary>تسجيل الخدمات المشتركة بين الموقع وخدمة العامل، حتى يعمل الاثنان بالإعدادات والمنطق نفسيهما.</summary>
public static class ServiceRegistration
{
    public const string DefaultConnectionString = "Data Source=App_Data/questionbank.db";

    public static string ConnectionString(IConfiguration config) =>
        config.GetConnectionString("QuestionBank") ?? DefaultConnectionString;

    public static IServiceCollection AddQuestionBankCore(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<InventoryOptions>().Bind(config.GetSection(InventoryOptions.SectionName)).ValidateDataAnnotations();
        services.AddOptions<UploadOptions>().Bind(config.GetSection(UploadOptions.SectionName)).ValidateDataAnnotations();
        services.AddOptions<AiOptions>().Bind(config.GetSection(AiOptions.SectionName)).ValidateDataAnnotations();

        // SQLite للتجربة. ملف القاعدة يجب أن يكون على قرص الخادم المحلي، وليس على مشاركة شبكة.
        services.AddDbContext<QuestionBankDbContext>(o => o.UseSqlite(ConnectionString(config)));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISourceFileSystem, PhysicalSourceFileSystem>();
        services.AddSingleton(ReaderRegistry.Default());
        services.AddSingleton<WorkerSignals>();
        services.AddScoped<WorkerCommandService>();
        services.AddScoped<InventoryScanner>();
        services.AddScoped<InventoryQueries>();
        services.AddScoped<FolderService>();
        services.AddScoped<ExtractionService>();
        services.AddScoped<UploadService>();
        services.AddScoped<QueueProcessor>();
        services.AddScoped<ReviewService>();
        services.AddScoped<ExportService>();
        services.AddScoped<DuplicateService>();
        services.AddScoped<ExcelReportService>();
        services.AddScoped<ColumnTemplateService>();
        services.AddScoped<FrameworkService>();

        services.AddSingleton<LexicalSuggester>();
        // الاقتراحات: نصية افتراضيًا، أو نموذج تضمين محلي عبر Ollama إذا ضُبط Provider=ollama بعنوان داخلي.
        if (string.Equals(config["QuestionBank:Ai:Provider"], "ollama", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<EmbeddingSuggester>();
            services.AddSingleton<IIndicatorSuggester>(sp => sp.GetRequiredService<EmbeddingSuggester>());
        }
        else
        {
            services.AddSingleton<IIndicatorSuggester>(sp => sp.GetRequiredService<LexicalSuggester>());
        }
        return services;
    }

    public static IServiceCollection AddQuestionBankWorkers(this IServiceCollection services)
    {
        services.AddHostedService<InventoryWorker>();
        services.AddHostedService<ExtractionWorker>();
        return services;
    }

    /// <summary>
    /// ينشئ مجلد القاعدة ويطبق الترحيلات. الموقع والعامل قد يبدآن معًا: إذا كانت القاعدة مقفلة نعيد المحاولة.
    /// </summary>
    public static async Task InitializeDatabaseAsync(IServiceProvider services, IConfiguration config, CancellationToken cancellationToken = default)
    {
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(ConnectionString(config)).DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<QuestionBankDbContext>();
                await db.Database.MigrateAsync(cancellationToken);
                // WAL: قراءة لوحة الحالة لا تنتظر كتابات الجرد.
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
                var duplicates = scope.ServiceProvider.GetRequiredService<DuplicateService>();
                await duplicates.BackfillAsync(cancellationToken);
                await duplicates.BackfillBandsAsync(cancellationToken);
                return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 10) // BUSY / LOCKED
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }
}
