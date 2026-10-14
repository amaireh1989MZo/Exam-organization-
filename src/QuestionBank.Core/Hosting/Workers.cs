using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuestionBank.Core.Data;
using QuestionBank.Core.Extraction;
using QuestionBank.Core.Inventory;

namespace QuestionBank.Core.Hosting;

/// <summary>
/// عامل الجرد. يقرأ الطلبات من قاعدة البيانات، ويستأنف أي عملية لم تكتمل عند التشغيل، ويكتب نبضه وحالته.
/// يعمل داخل الموقع أو داخل خدمة Windows منفصلة بالمنطق نفسه.
/// </summary>
public sealed class InventoryWorker(
    IServiceScopeFactory scopes,
    WorkerSignals signals,
    IOptions<InventoryOptions> options,
    TimeProvider time,
    ILogger<InventoryWorker> logger) : BackgroundService
{
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public static TimeSpan SourceRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    private bool _resumePending;
    private DateTime _retryAtUtc = DateTime.MinValue;
    private string? _problemCode;
    private string? _problemMessage;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuestionBankDbContext>();
            _resumePending = await db.ScanRuns.AnyAsync(r => r.Status == ScanRunStatus.Running, stoppingToken) || options.Value.ScanOnStartup;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReportAsync(false, stoppingToken);
                var command = await WithCommands(c => c.TakeNextAsync(stoppingToken));
                if (command?.Kind == WorkerCommandKinds.Abandon)
                {
                    await AbandonAsync(stoppingToken);
                    continue;
                }
                if (command?.Kind == WorkerCommandKinds.Scan || (_resumePending && time.GetUtcNow().UtcDateTime >= _retryAtUtc))
                {
                    _resumePending = false;
                    await ScanAsync(stoppingToken);
                    continue;
                }
                await signals.WaitInventoryAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Inventory worker loop failed with {ExceptionType}.", ex.GetType().Name);
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task ScanAsync(CancellationToken stoppingToken)
    {
        await WithCommands(c => c.CoalesceScansAsync(stoppingToken));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var watcher = WatchAsync(cts, stoppingToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<InventoryScanner>().RunAsync(cts.Token);
            SetProblem(null, null);
        }
        catch (InventoryConfigurationException ex)
        {
            SetProblem("Configuration", string.Join(" ", ex.Errors));
            logger.LogError("Inventory not started: configuration is invalid ({ErrorCount} problems).", ex.Errors.Count);
        }
        catch (SourceUnavailableException)
        {
            SetProblem(InventoryErrorCodes.SourceUnavailable, "مجلد المصدر غير متاح. سيُعاد المحاولة تلقائيًا.");
            logger.LogWarning("Source root unavailable; inventory will retry in {Delay}.", SourceRetryDelay);
            _resumePending = true;
            _retryAtUtc = time.GetUtcNow().UtcDateTime + SourceRetryDelay;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Inventory scan interrupted by an abandon request.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetProblem(InventoryErrorCodes.Unexpected, "حدث خطأ غير متوقع أثناء الجرد. راجع سجل الخادم.");
            logger.LogError("Inventory scan failed with {ExceptionType}.", ex.GetType().Name);
        }
        finally
        {
            await cts.CancelAsync();
            try { await watcher; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>أثناء الجرد: نبض دوري، وإيقاف الجرد إذا وصل طلب تخلٍّ (يُعالَج الطلب نفسه بعد التوقف).</summary>
    private async Task WatchAsync(CancellationTokenSource scan, CancellationToken stoppingToken)
    {
        while (!scan.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, scan.Token);
            await ReportAsync(true, stoppingToken);
            if (await WithCommands(c => c.HasPendingAbandonAsync(stoppingToken)))
            {
                await scan.CancelAsync();
                return;
            }
        }
    }

    private async Task AbandonAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<InventoryScanner>().AbandonActiveRunAsync(stoppingToken);
        _resumePending = false;
        SetProblem(null, null);
    }

    private void SetProblem(string? code, string? message)
    {
        _problemCode = code;
        _problemMessage = message;
    }

    private Task ReportAsync(bool busy, CancellationToken ct) =>
        WithCommands(c => c.ReportAsync(WorkerCommandService.InventoryWorkerName, busy, _problemCode, _problemMessage, ct));

    private async Task<T> WithCommands<T>(Func<WorkerCommandService, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<WorkerCommandService>());
    }

    private async Task WithCommands(Func<WorkerCommandService, Task> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<WorkerCommandService>());
    }
}

/// <summary>
/// عامل الاستخراج: يستهلك طابور المجلدات دفعة بعد دفعة، ويستأنف من حيث وقف لأن حالة كل ملف وطلب محفوظة.
/// </summary>
public sealed class ExtractionWorker(
    IServiceScopeFactory scopes,
    WorkerSignals signals,
    ILogger<ExtractionWorker> logger) : BackgroundService
{
    public static TimeSpan IdleWait { get; set; } = TimeSpan.FromSeconds(10);
    public static TimeSpan SourceRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string? problem = null;
            try
            {
                bool worked;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<WorkerCommandService>()
                        .ReportAsync(WorkerCommandService.ExtractionWorkerName, true, null, null, stoppingToken);
                    worked = await scope.ServiceProvider.GetRequiredService<QueueProcessor>().ProcessNextBatchAsync(stoppingToken);
                }
                if (!worked)
                {
                    await Report(false, null, stoppingToken);
                    await signals.WaitExtractionAsync(IdleWait, stoppingToken);
                }
                continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (SourceUnavailableException)
            {
                problem = "مجلد المصدر غير متاح. سيُعاد المحاولة تلقائيًا.";
                logger.LogWarning("Source root unavailable; extraction will retry in {Delay}.", SourceRetryDelay);
            }
            catch (Exception ex)
            {
                // لا نسجل الرسالة: قد تحتوي مسارًا أو نصًا من ملف.
                problem = "حدث خطأ غير متوقع أثناء الاستخراج. راجع سجل الخادم.";
                logger.LogError("Extraction batch failed with {ExceptionType}.", ex.GetType().Name);
            }
            await Report(false, problem, stoppingToken);
            await Task.Delay(SourceRetryDelay, stoppingToken);
        }
    }

    private async Task Report(bool busy, string? problem, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WorkerCommandService>()
            .ReportAsync(WorkerCommandService.ExtractionWorkerName, busy, problem is null ? null : "Problem", problem, ct);
    }
}
