using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Services;

/// <summary>
/// Cron 计划任务后台调度器。
/// </summary>
public sealed class ScheduledTaskBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledTaskBackgroundService> _logger;

    public ScheduledTaskBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledTaskBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "计划任务调度循环执行失败");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var scheduledTaskService = scope.ServiceProvider.GetRequiredService<ScheduledTaskService>();
        var batchTaskManagement = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();

        var enabledTasks = await scheduledTaskService.GetEnabledAsync(cancellationToken);
        if (enabledTasks.Count == 0)
            return;

        var nowUtc = DateTime.UtcNow;
        foreach (var scheduledTask in enabledTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nextRunAtUtc = scheduledTask.NextRunAtUtc;
            if (!nextRunAtUtc.HasValue)
            {
                await scheduledTaskService.AdvanceNextRunAsync(scheduledTask.Id, nowUtc, cancellationToken);
                continue;
            }

            if (nextRunAtUtc.Value > nowUtc)
                continue;

            if (scheduledTask.LastBatchTaskId.HasValue)
            {
                var lastBatchTask = await batchTaskManagement.GetTaskAsync(scheduledTask.LastBatchTaskId.Value);
                if (lastBatchTask != null && lastBatchTask.Status is "pending" or "running" or "paused")
                {
                    _logger.LogInformation(
                        "计划任务 {ScheduledTaskName}（{ScheduledTaskId}）上次执行尚未结束，本轮跳过",
                        scheduledTask.Name,
                        scheduledTask.Id);
                    await scheduledTaskService.AdvanceNextRunAsync(scheduledTask.Id, nowUtc, cancellationToken);
                    continue;
                }
            }

            var task = new BatchTask
            {
                Name = NormalizeBatchTaskName(scheduledTask.Name),
                TaskType = scheduledTask.TaskType,
                Total = Math.Max(0, scheduledTask.Total),
                Completed = 0,
                Failed = 0,
                Config = TaskAssetScopeHelper.RemoveAssetScopeId(scheduledTask.ConfigJson)
            };

            var created = await batchTaskManagement.CreateTaskAsync(task);
            _logger.LogInformation(
                "计划任务触发：name={ScheduledTaskName}, schedule={ScheduledTaskId}, batch={BatchTaskId}, type={TaskType}",
                scheduledTask.Name,
                scheduledTask.Id,
                created.Id,
                created.TaskType);
            await scheduledTaskService.MarkTriggeredAsync(scheduledTask.Id, nowUtc, created.Id, cancellationToken);
        }
    }

    private static string? NormalizeBatchTaskName(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        return value.Length == 0 ? null : value;
    }
}
