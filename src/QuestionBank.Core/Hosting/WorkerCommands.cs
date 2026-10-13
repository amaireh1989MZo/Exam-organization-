using Microsoft.EntityFrameworkCore;
using QuestionBank.Core.Data;

namespace QuestionBank.Core.Hosting;

public static class WorkerCommandKinds
{
    public const string Scan = "Scan";
    public const string Abandon = "Abandon";
}

/// <summary>
/// طلب للعامل الخلفي عبر قاعدة البيانات، فيصل سواء كان العامل داخل الموقع أو خدمة Windows منفصلة.
/// </summary>
public sealed class WorkerCommand
{
    public long Id { get; set; }
    public required string Kind { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}

/// <summary>حالة عامل معين ونبضه الأخير. الموقع يقرؤها ليعرف هل العامل يعمل أصلًا.</summary>
public sealed class WorkerState
{
    public required string Name { get; set; }
    public bool Busy { get; set; }
    public string? ProblemCode { get; set; }
    public string? ProblemMessage { get; set; }
    public DateTime HeartbeatUtc { get; set; }
    public required string Host { get; set; }
}

public sealed record WorkerStatus(string Name, bool Running, bool Busy, string? ProblemCode, string? Problem, DateTime? HeartbeatUtc, string? Host);

/// <summary>إشارات توقظ العامل فورًا إذا كان في العملية نفسها؛ وإلا يلتقط الطلب في دورة الاستطلاع التالية.</summary>
public sealed class WorkerSignals
{
    private readonly SemaphoreSlim _inventory = new(0, 1);
    private readonly SemaphoreSlim _extraction = new(0, 1);

    public void WakeInventory() => Release(_inventory);
    public void WakeExtraction() => Release(_extraction);

    public Task<bool> WaitInventoryAsync(TimeSpan timeout, CancellationToken ct) => _inventory.WaitAsync(timeout, ct);
    public Task<bool> WaitExtractionAsync(TimeSpan timeout, CancellationToken ct) => _extraction.WaitAsync(timeout, ct);

    private static void Release(SemaphoreSlim s)
    {
        if (s.CurrentCount == 0)
        {
            try { s.Release(); } catch (SemaphoreFullException) { }
        }
    }
}

public sealed class WorkerCommandService(QuestionBankDbContext db, TimeProvider time)
{
    public const string InventoryWorkerName = "inventory";
    public const string ExtractionWorkerName = "extraction";

    /// <summary>نبض أقدم من هذا يعني أن العامل متوقف.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

    public async Task EnqueueAsync(string kind, string user, CancellationToken cancellationToken)
    {
        db.WorkerCommands.Add(new WorkerCommand { Kind = kind, RequestedBy = user, RequestedAtUtc = time.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    /// <summary>أقدم طلب لم يُعالج، ويُعلَّم معالجًا في الحال (لا يأخذه عاملان).</summary>
    public async Task<WorkerCommand?> TakeNextAsync(CancellationToken cancellationToken)
    {
        var cmd = await db.WorkerCommands.Where(c => c.ProcessedAtUtc == null).OrderBy(c => c.Id).FirstOrDefaultAsync(cancellationToken);
        if (cmd is null)
            return null;
        cmd.ProcessedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return cmd;
    }

    /// <summary>يعلّم كل طلبات الجرد المعلقة معالجة (عند بدء جرد تُدمج الطلبات المكررة في جرد واحد).</summary>
    public async Task<int> CoalesceScansAsync(CancellationToken cancellationToken) =>
        await db.WorkerCommands.Where(c => c.ProcessedAtUtc == null && c.Kind == WorkerCommandKinds.Scan)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProcessedAtUtc, time.GetUtcNow().UtcDateTime), cancellationToken);

    public Task<bool> HasPendingAbandonAsync(CancellationToken cancellationToken) =>
        db.WorkerCommands.AnyAsync(c => c.ProcessedAtUtc == null && c.Kind == WorkerCommandKinds.Abandon, cancellationToken);

    public async Task ReportAsync(string name, bool busy, string? problemCode, string? problemMessage, CancellationToken cancellationToken)
    {
        var state = await db.WorkerStates.FirstOrDefaultAsync(s => s.Name == name, cancellationToken);
        if (state is null)
        {
            state = new WorkerState { Name = name, Host = Environment.MachineName };
            db.WorkerStates.Add(state);
        }
        state.Busy = busy;
        state.ProblemCode = problemCode;
        state.ProblemMessage = problemMessage;
        state.HeartbeatUtc = time.GetUtcNow().UtcDateTime;
        state.Host = Environment.MachineName;
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    public async Task<WorkerStatus> StatusAsync(string name, CancellationToken cancellationToken)
    {
        var s = await db.WorkerStates.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name, cancellationToken);
        if (s is null)
            return new WorkerStatus(name, false, false, "WorkerNotStarted", "عامل المعالجة لم يعمل بعد. تأكد من تشغيل خدمة العامل أو تفعيله داخل الموقع.", null, null);
        var running = time.GetUtcNow().UtcDateTime - s.HeartbeatUtc <= StaleAfter;
        return running
            ? new WorkerStatus(name, true, s.Busy, s.ProblemCode, s.ProblemMessage, s.HeartbeatUtc, s.Host)
            : new WorkerStatus(name, false, false, "WorkerStopped", "عامل المعالجة متوقف (لا نبض حديث). تحقق من خدمة العامل على الخادم.", s.HeartbeatUtc, s.Host);
    }
}
