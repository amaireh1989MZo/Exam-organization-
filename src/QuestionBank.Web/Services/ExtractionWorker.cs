using QuestionBank.Core.Extraction;
using QuestionBank.Core.Inventory;

namespace QuestionBank.Web.Services;

/// <summary>إشارة توقظ عامل الاستخراج فور إضافة طلب، بدل انتظار الدورة التالية.</summary>
public sealed class ExtractionSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// يستهلك طابور المعالجة دفعة بعد دفعة. عند التوقف يكمل الطابور من حيث وقف، لأن حالة كل ملف وكل طلب محفوظة.
/// </summary>
public sealed class ExtractionWorker(
    IServiceScopeFactory scopes,
    ExtractionSignal signal,
    ILogger<ExtractionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SourceRetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool worked;
                using (var scope = scopes.CreateScope())
                    worked = await scope.ServiceProvider.GetRequiredService<QueueProcessor>().ProcessNextBatchAsync(stoppingToken);
                if (!worked)
                    await signal.WaitAsync(IdleWait, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (SourceUnavailableException)
            {
                logger.LogWarning("Source root unavailable; extraction will retry in {Delay}.", SourceRetryDelay);
                await Task.Delay(SourceRetryDelay, stoppingToken);
            }
            catch (Exception ex)
            {
                // لا نسجل الرسالة: قد تحتوي مسارًا أو نصًا من ملف.
                logger.LogError("Extraction batch failed with {ExceptionType}.", ex.GetType().Name);
                await Task.Delay(SourceRetryDelay, stoppingToken);
            }
        }
    }
}
