using TelegramPanel.Core.Services;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 在所有任务执行通道之间共享一次启动恢复，避免 batch/persistent 分别恢复产生竞争。
/// </summary>
public sealed class BatchTaskStartupRecoveryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BatchTaskStartupRecoveryService> _logger;
    private readonly ModuleTaskLifecycleService? _lifecycle;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _recovered;

    public BatchTaskStartupRecoveryService(
        IServiceScopeFactory scopeFactory,
        ILogger<BatchTaskStartupRecoveryService> logger,
        ModuleTaskLifecycleService? lifecycle = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _lifecycle = lifecycle;
    }

    public async Task EnsureRecoveredAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _recovered))
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_recovered)
                return;

            using var scope = _scopeFactory.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            var result = await tasks.RecoverInterruptedTasksAsync(cancellationToken);
            if (_lifecycle != null)
                await _lifecycle.ReconcileAsync(cancellationToken);
            _recovered = true;
            _logger.LogInformation(
                "Recovered interrupted tasks: requeued={Requeued}, confirmedPaused={Paused}",
                result.Requeued,
                result.Paused);
        }
        finally
        {
            _gate.Release();
        }
    }
}
