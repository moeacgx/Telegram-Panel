using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 批量任务后台执行器：从数据库拉取 pending 任务并在后台静默运行。
/// </summary>
public sealed class BatchTaskBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BatchTaskBackgroundService> _logger;
    private readonly IConfiguration _configuration;
    private readonly BatchTaskExecutionControlService _executionControl;
    private readonly BatchTaskStartupRecoveryService? _startupRecovery;
    private readonly TelegramPanel.Web.Modules.ModuleContributionRegistry? _contributions;
    private readonly ConcurrentDictionary<int, Task> _runningTasks = new();

    public BatchTaskBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        BatchTaskExecutionControlService executionControl,
        ILogger<BatchTaskBackgroundService> logger,
        BatchTaskStartupRecoveryService? startupRecovery = null,
        TelegramPanel.Web.Modules.ModuleContributionRegistry? contributions = null)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _executionControl = executionControl;
        _logger = logger;
        _startupRecovery = startupRecovery;
        _contributions = contributions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue("BatchTasks:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("Batch task runner disabled (BatchTasks:Enabled=false)");
            return;
        }

        var seconds = _configuration.GetValue("BatchTasks:PollIntervalSeconds", 2);
        if (seconds < 1) seconds = 1;
        if (seconds > 30) seconds = 30;
        var interval = TimeSpan.FromSeconds(seconds);

        var initialMaxConcurrent = ReadMaxConcurrent();

        _logger.LogInformation(
            "Batch task runner started, interval {IntervalSeconds} seconds, maxConcurrent {MaxConcurrent}",
            seconds,
            initialMaxConcurrent);

        // 延迟一点，避免与启动时 DB 迁移抢资源
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        if (_startupRecovery != null)
            await _startupRecovery.EnsureRecoveredAsync(stoppingToken);
        else
            await RecoverInterruptedTasksAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 先清理已结束的任务，避免“占位”导致无法继续启动新任务
                CleanupCompletedTasks();
                var maxConcurrent = ReadMaxConcurrent();

                while (_runningTasks.Count < maxConcurrent)
                {
                    var started = await TryStartOneAsync(stoppingToken);
                    if (!started)
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch task runner loop failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private int ReadMaxConcurrent()
    {
        var maxConcurrent = _configuration.GetValue("BatchTasks:MaxConcurrent", 1);
        if (maxConcurrent < 1) return 1;
        if (maxConcurrent > 10) return 10;
        return maxConcurrent;
    }

    private async Task RecoverInterruptedTasksAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var taskManagement = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
        var requeued = await taskManagement.RequeueRunningTasksAsync(cancellationToken: cancellationToken);
        if (requeued > 0)
        {
            _logger.LogInformation("Recovered {Count} interrupted running batch tasks and set them back to pending", requeued);
        }
    }

    private void CleanupCompletedTasks()
    {
        foreach (var kv in _runningTasks)
        {
            var task = kv.Value;
            if (!task.IsCompleted && !task.IsCanceled && !task.IsFaulted)
                continue;

            _runningTasks.TryRemove(kv.Key, out _);
        }
    }

    private async Task<bool> TryStartOneAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var taskManagement = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
        var supportedTaskTypes = scope.ServiceProvider
            .GetServices<IModuleTaskHandler>()
            .Where(handler => !string.IsNullOrWhiteSpace(handler.TaskType))
            .GroupBy(handler => handler.TaskType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pending = (await taskManagement.GetTasksByStatusAsync("pending"))
            .Where(t => string.Equals(t.ExecutionKind, ModuleTaskExecutionKinds.Batch, StringComparison.OrdinalIgnoreCase))
            .Where(IsOwnedBatchTask)
            .Where(t => supportedTaskTypes.Contains(t.TaskType))
            .Where(t => !_runningTasks.ContainsKey(t.Id))
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefault();

        if (pending == null)
            return false;

        var execution = await _executionControl.TryStartExecutionAsync(pending.Id, cancellationToken);
        if (execution == null)
            return false;

        _logger.LogInformation("Batch task started: {TaskId} {TaskType}", pending.Id, pending.TaskType);

        // 独立 scope 执行：避免阻塞轮询 loop，实现并发跑多个任务
        try
        {
            var running = RunTaskAsync(pending.Id, execution, cancellationToken);
            _runningTasks[pending.Id] = running;
        }
        catch
        {
            _executionControl.CompleteExecution(execution);
            throw;
        }

        return true;
    }

    private async Task RunTaskAsync(
        int taskId,
        BatchTaskExecutionLease execution,
        CancellationToken stoppingToken)
    {
        var executionToken = execution.CancellationToken;
        using var executionContext = _executionControl.EnterExecutionContext(execution);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var taskManagement = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();

            var pending = await taskManagement.GetTaskAsync(taskId);
            if (pending == null || pending.Status != "running")
                return;

            executionToken.ThrowIfCancellationRequested();
            var completed = pending.Completed;
            var failed = pending.Failed;

            try
            {
                var handlers = scope.ServiceProvider
                    .GetServices<IModuleTaskHandler>()
                    .Where(h => string.Equals(h.TaskType, pending.TaskType, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (handlers.Count != 1)
                {
                    failed = pending.Total == 0 ? 1 : pending.Total;
                    await taskManagement.UpdateTaskProgressAsync(pending.Id, completed, failed);
                    await taskManagement.CompleteTaskAsync(pending.Id, success: false);
                    _logger.LogWarning(
                        "Batch task requires exactly one handler: {TaskType} (task {TaskId}, count={Count})",
                        pending.TaskType,
                        pending.Id,
                        handlers.Count);
                    return;
                }

                var host = new DbBackedModuleTaskExecutionHost(pending, taskManagement, scope.ServiceProvider);
                await handlers[0].ExecuteAsync(host, executionToken);
                executionToken.ThrowIfCancellationRequested();

                var after = await taskManagement.GetTaskAsync(pending.Id);
                if (after != null)
                {
                    completed = after.Completed;
                    failed = after.Failed;
                }

                var latest = await taskManagement.GetTaskAsync(pending.Id);
                if (latest != null && latest.Status != "running")
                    return;

                if (latest != null && IsPersistentTask(latest))
                {
                    var requeued = await taskManagement.RequeueRunningTasksAsync(
                        t => t.Id == pending.Id,
                        executionToken);
                    _logger.LogWarning(
                        "Persistent batch task returned without explicit completion; requeued instead of completing: {TaskId} {TaskType} (requeued={Requeued})",
                        pending.Id,
                        pending.TaskType,
                        requeued);
                    return;
                }

                await taskManagement.CompleteTaskAsync(pending.Id, success: true);
                _logger.LogInformation("Batch task completed: {TaskId} {TaskType} (completed={Completed}, failed={Failed})",
                    pending.Id, pending.TaskType, completed, failed);
            }
            catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Batch task interrupted by shutdown: {TaskId} {TaskType}", pending.Id, pending.TaskType);
                }
                else
                {
                    _logger.LogInformation("Batch task execution stopped after pause: {TaskId} {TaskType}", pending.Id, pending.TaskType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch task failed: {TaskId} {TaskType}", pending.Id, pending.TaskType);
                try
                {
                    await taskManagement.UpdateTaskProgressAsync(pending.Id, completed, failed == 0 ? 1 : failed);
                    await taskManagement.CompleteTaskAsync(pending.Id, success: false);
                }
                catch
                {
                    // 忽略二次收尾错误。
                }
            }
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            // 宿主停机或暂停时由上层状态屏障负责收尾。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch task execution crashed (taskId={TaskId})", taskId);
        }
        finally
        {
            _runningTasks.TryRemove(taskId, out _);
            await _executionControl.CompleteExecutionAsync(execution);
            var keepCount = _configuration.GetValue("BatchTasks:HistoryRetentionLimit", 0);
            if (keepCount > 0 && !stoppingToken.IsCancellationRequested)
                await _executionControl.TrimHistoryTasksAsync(keepCount);
        }
    }

    private bool IsOwnedBatchTask(BatchTask task)
    {
        if (string.Equals(task.OwnerModuleId, "host.legacy", StringComparison.Ordinal))
            return true;
        if (_contributions == null
            || !_contributions.TaskTypeToDefinition.TryGetValue(task.TaskType, out var registered))
            return false;

        return string.Equals(registered.Module.Id, task.OwnerModuleId, StringComparison.Ordinal)
            && string.Equals(
                registered.Definition.ExecutionKind,
                ModuleTaskExecutionKinds.Batch,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPersistentTask(BatchTask task)
    {
        if (!string.Equals(task.TaskType, BatchTaskTypes.UserChatActive, StringComparison.OrdinalIgnoreCase))
            return false;

        var config = (task.Config ?? string.Empty).Trim();
        if (config.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(config);
                if (document.RootElement.TryGetProperty("max_messages", out var maxMessages)
                    && maxMessages.ValueKind == JsonValueKind.Number
                    && maxMessages.TryGetInt32(out var value))
                {
                    return value <= 0;
                }
            }
            catch (JsonException)
            {
                // Config 无法解析时回退到 Total，避免异常遮蔽任务本身的收尾逻辑。
            }
        }

        return task.Total <= 0;
    }

    private sealed class DbBackedModuleTaskExecutionHost : IModuleTaskExecutionHost
    {
        private readonly BatchTask _task;
        private readonly BatchTaskManagementService _taskManagement;

        public DbBackedModuleTaskExecutionHost(BatchTask task, BatchTaskManagementService taskManagement, IServiceProvider services)
        {
            _task = task;
            _taskManagement = taskManagement;
            Services = services;
        }

        public int TaskId => _task.Id;
        public string TaskType => _task.TaskType;
        public int Total => _task.Total;
        public string? Config => _task.Config;
        public IServiceProvider Services { get; }

        public async Task<bool> IsStillRunningAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = await _taskManagement.GetTaskAsync(_task.Id);
            return latest != null && latest.Status == "running";
        }

        public async Task UpdateProgressAsync(int completed, int failed, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _taskManagement.UpdateTaskProgressAsync(_task.Id, completed, failed);
        }
    }
}
