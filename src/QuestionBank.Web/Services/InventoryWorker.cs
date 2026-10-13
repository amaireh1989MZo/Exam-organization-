using System.Threading.Channels;
using QuestionBank.Core.Data;
using QuestionBank.Core.Inventory;

namespace QuestionBank.Web.Services;

public enum InventoryCommand
{
    Scan,
    Abandon
}

/// <summary>
/// نقطة التواصل بين الواجهة والعامل الخلفي: طلبات بدء الجرد أو التخلي عنه، وحالة العامل الحالية.
/// </summary>
public sealed class InventoryCoordinator
{
    private readonly Channel<InventoryCommand> _commands = Channel.CreateUnbounded<InventoryCommand>();
    private CancellationTokenSource? _currentScan;

    public ChannelReader<InventoryCommand> Commands => _commands.Reader;
    public bool IsScanning { get; private set; }

    /// <summary>آخر مشكلة منعت الجرد، برمز ثابت ورسالة لا تكشف المسارات.</summary>
    public string? LastProblemCode { get; private set; }
    public string? LastProblemMessage { get; private set; }

    public void RequestScan() => _commands.Writer.TryWrite(InventoryCommand.Scan);

    public void RequestAbandon()
    {
        _currentScan?.Cancel();
        _commands.Writer.TryWrite(InventoryCommand.Abandon);
    }

    internal CancellationTokenSource BeginScan(CancellationToken stopping)
    {
        _currentScan = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        IsScanning = true;
        return _currentScan;
    }

    internal void EndScan()
    {
        IsScanning = false;
        _currentScan?.Dispose();
        _currentScan = null;
    }

    internal void ReportProblem(string? code, string? message)
    {
        LastProblemCode = code;
        LastProblemMessage = message;
    }
}

/// <summary>
/// العامل الخلفي للجرد. عند التشغيل يستأنف أي عملية لم تكتمل (بعد توقف الخدمة أو إعادة تشغيل الخادم)،
/// ثم ينتظر الطلبات. في الإنتاج يمكن نقله إلى Windows Service مستقلة بالمنطق نفسه.
/// </summary>
public sealed class InventoryWorker(
    IServiceScopeFactory scopes,
    InventoryCoordinator coordinator,
    Microsoft.Extensions.Options.IOptions<InventoryOptions> options,
    ILogger<InventoryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SourceRetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuestionBankDbContext>();
            var hasActiveRun = db.ScanRuns.Any(r => r.Status == ScanRunStatus.Running);
            if (hasActiveRun || options.Value.ScanOnStartup)
                coordinator.RequestScan();
        }

        await foreach (var command in coordinator.Commands.ReadAllAsync(stoppingToken))
        {
            if (command == InventoryCommand.Abandon)
            {
                await AbandonAsync(stoppingToken);
                continue;
            }
            if (coordinator.IsScanning)
                continue;
            await ScanAsync(stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken stoppingToken)
    {
        using var cts = coordinator.BeginScan(stoppingToken);
        try
        {
            using var scope = scopes.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<InventoryScanner>();
            await scanner.RunAsync(cts.Token);
            coordinator.ReportProblem(null, null);
        }
        catch (InventoryConfigurationException ex)
        {
            coordinator.ReportProblem("Configuration", string.Join(" ", ex.Errors));
            logger.LogError("Inventory not started: configuration is invalid ({ErrorCount} problems).", ex.Errors.Count);
        }
        catch (SourceUnavailableException)
        {
            coordinator.ReportProblem(InventoryErrorCodes.SourceUnavailable, "مجلد المصدر غير متاح. سيُعاد المحاولة تلقائيًا.");
            logger.LogWarning("Source root unavailable; inventory will retry in {Delay}.", SourceRetryDelay);
            _ = RetryLaterAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Inventory scan interrupted on request.");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Inventory scan paused for shutdown; it will resume on next start.");
        }
        catch (Exception ex)
        {
            // لا نسجل الرسالة: قد تحتوي مسارًا. النوع يكفي للتشخيص الأولي.
            coordinator.ReportProblem(InventoryErrorCodes.Unexpected, "حدث خطأ غير متوقع أثناء الجرد. راجع سجل الخادم.");
            logger.LogError("Inventory scan failed with {ExceptionType}.", ex.GetType().Name);
        }
        finally
        {
            coordinator.EndScan();
        }
    }

    private async Task RetryLaterAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(SourceRetryDelay, stoppingToken);
            coordinator.RequestScan();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AbandonAsync(CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService<InventoryScanner>();
        await scanner.AbandonActiveRunAsync(stoppingToken);
    }
}
